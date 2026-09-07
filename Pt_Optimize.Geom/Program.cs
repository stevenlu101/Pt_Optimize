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
            int rc;
            try { rc = RunThickness(f3, layer, yPlane, step); }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); rc = 2; }
            Bye(rc);      // ★ 带上**真正的**返回码退出
            return rc;    // 到不了，编译器要
        }

        // plate 模式：把优化搜出来的**解析法兰**写成 .3dm（与 thickness 模式反向）
        //   Pt_Optimize.Geom.exe plate <out.3dm> <盘半径> <孔半径> <舌端X> <舌端半宽> <厚度1[,厚度2,…]>
        // 每个厚度出一个实体，沿 +X 依次排开、各自独立成体，图层统一为「法兰」。
        // 轮廓 = 盘圆弧（切点之外那段）+ 舌片两条直边 + 舌端直边，中心挖孔，再拉伸。
        // final 模式：按**主程序导出的 JSON 规格**渲染设计记录整机几何
        //   Pt_Optimize.Geom.exe final <spec.json> <out.3dm>
        //
        // 几何定义**不在这里**。规格由 Pt_Optimize 从 Core/DesignSpec 导出，
        // 本进程只负责渲染 —— 否则设计记录值就在两个项目里各存一份，
        // 而「同一个数抄两处然后悄悄漂开」正是本项目最常见的失效（HANDOVER 1.8）。
        if (args.Length > 0 && args[0] == "final")
        {
            if (args.Length < 3)
            { Console.Error.WriteLine("用法：Pt_Optimize.Geom.exe final <spec.json> <out.3dm>"); return 64; }
            string specPath = args[1], outFinal = args[2];
            if (!File.Exists(specPath))
            { Console.Error.WriteLine("规格文件不存在：" + specPath); return 64; }
            string specJson = File.ReadAllText(specPath, Encoding.UTF8);
            try { RhinoInside.Resolver.Initialize(); }
            catch (Exception e) { Console.Error.WriteLine("Resolver 失败：" + e.Message); return 1; }
            int rc;
            try { rc = RunFinal(specJson, outFinal); }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); rc = 2; }
            Bye(rc);      // ★ 带上**真正的**返回码退出
            return rc;    // 到不了，编译器要
        }

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
            int rc;
            try { rc = RunPlate(outPath, discR, holeR, tabX, tabHW, thicks); }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); rc = 2; }
            Bye(rc);      // ★ 带上**真正的**返回码退出
            return rc;    // 到不了，编译器要
        }

        // steps 模式：写一个**阶梯厚度 + 开槽**的法兰，各级为**独立实体**
        //   Geom.exe steps <out.3dm> <孔R> <r1,r2,..> <t1,t2,..> <舌端X> <舌端半宽> <舌厚> [槽数] [槽角宽] [槽r内,槽r外]
        // 用来复现「R60/t3 -> R46/t2 -> R36/t1 + 四槽」这类图纸，供逐级定厚验证。
        if (args.Length > 0 && args[0] == "steps")
        {
            if (args.Length < 8)
            {
                Console.Error.WriteLine("用法：Geom.exe steps <out.3dm> <孔R> <r1,r2,..> <t1,t2,..> <舌端X> <舌端半宽> <舌厚> [槽数] [槽角宽] [槽r内,槽r外]");
                return 64;
            }
            string sOut = args[1];
            double.TryParse(args[2], out double sHole);
            var sR = args[3].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var sT = args[4].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            double.TryParse(args[5], out double sTabX);
            double.TryParse(args[6], out double sTabHW);
            double.TryParse(args[7], out double sTabT);
            int sN = args.Length > 8 && int.TryParse(args[8], out var nn) ? nn : 0;
            double sDeg = args.Length > 9 && double.TryParse(args[9], out var dd) ? dd : 20;
            double sSlotIn = sHole, sSlotOut = sR.Length > 0 ? sR[0] : sHole;
            if (args.Length > 10)
            {
                var pr = args[10].Split(',');
                if (pr.Length == 2) { double.TryParse(pr[0], out sSlotIn); double.TryParse(pr[1], out sSlotOut); }
            }
            if (sR.Length != sT.Length || sR.Length == 0)
            { Console.Error.WriteLine("半径与厚度数量不一致"); return 64; }

            try { RhinoInside.Resolver.Initialize(); }
            catch (Exception e) { Console.Error.WriteLine("Resolver 失败：" + e.Message); return 1; }
            // 第 11 个参数：舌型（par = 等宽，与 FlangePlate.TabParallel 同口径；缺省梯形，保持旧行为）
            bool sPar = args.Length > 11 && args[11].Equals("par", StringComparison.OrdinalIgnoreCase);
            // ★ 第 12 个参数：舌孔「孔心x,孔半径」（2026-09-05，用户要求 R5）
            var sHoles = new System.Collections.Generic.List<(double X, double Z, double R)>();
            if (args.Length > 12)
            {
                var hp = args[12].Split(',');
                if (hp.Length == 2
                    && double.TryParse(hp[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double hX)
                    && double.TryParse(hp[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double hR)
                    && hR > 1e-6)
                    sHoles.Add((hX, 0, hR));
            }
            int rc;
            try { rc = RunSteps(sOut, sHole, sR, sT, sTabX, sTabHW, sTabT, sN, sDeg, sSlotIn, sSlotOut, sPar, sHoles); }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); rc = 2; }
            Bye(rc);      // ★ 带上**真正的**返回码退出
            return rc;    // 到不了，编译器要
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
            int rc;
            try { rc = RunScale(inP, outP, lay, kScale, planeYs, kList); }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); rc = 2; }
            Bye(rc);      // ★ 带上**真正的**返回码退出
            return rc;    // 到不了，编译器要
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

        int rcMain;
        try { rcMain = Run(path); }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.GetType().Name + ": " + e.Message);
            rcMain = 2;
        }
        Bye(rcMain);
        return rcMain;    // 到不了，编译器要
    }

    /// <summary>
    /// 强制退出，**并且带上真正的返回码**。
    ///
    /// ★ 2026-08-25 查出的真事：这里原本写的是
    ///     finally { Console.Out.Flush(); Environment.Exit(Environment.ExitCode); }
    ///   而 <c>Environment.ExitCode</c> 是**另一个属性**，默认恒为 0 ——
    ///   `return 4` 不会写进它。于是 finally 里那句等价于 Environment.Exit(0)，
    ///   **把每一种失败码都抹成成功**（thickness 的「图层无实体」是 4、异常是 2）。
    ///   六个模式全是这个写法 ⇒ 这个子进程**根本没有能力向主程序报告失败**。
    ///
    ///   现场表现：拿设计记录自己的图纸去跑 thickness，stderr 明明写着「图层无实体：法兰」，
    ///   退出码却是 0、stdout 为空 ⇒ 主程序 `ExitCode != 0` 检查通过，
    ///   转头拿空字符串解析 JSON，崩在 System.Text.Json，与真因隔了三层。
    ///
    /// ⚠ 强制退出本身是**必要**的，不能删：RhinoCore 的前台线程会挡住进程自然退出
    ///   （房规 §6 坑 3）。要改的只是**退出码**。
    /// </summary>
    private static void Bye(int rc)
    {
        Console.Out.Flush();
        Console.Error.Flush();
        Environment.Exit(rc);
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
            if (breps.Count == 0)
            {
                // ★ 把**现有图层**一并报出来（2026-08-25）。scale 模式一直这么做，
                //   thickness 模式却只说「无实体」—— 同一个程序里两种口径，
                //   而拿设计记录 3DM 撞上来的正是 thickness 这一侧：
                //   WriteFinal3dm 写的是「入口-板身／入口-环外级／…」这样按**部位**分的
                //   22 个图层，而本模式要的是**整片一个图层**。不列出来，人无从猜起。
                Console.Error.WriteLine("图层无实体：" + layerName + "。现有图层："
                    + string.Join("、", Enumerable.Range(0, doc.Layers.Count)
                                                  .Select(i2 => doc.Layers[i2].Name)));
                return 4;
            }

            // ★ 一片法兰可能由**多个独立实体**拼成（阶梯厚度常这么画：每级一个环）。
            //   早先只量 breps[pick] 一个实体，于是阶梯件里**厚度不同的那几级被整个漏掉**
            //   （实测：R36/t1 那级完全没读到，因为射线只对着 t=3 那个实体的中面）。
            //   现按 XZ 投影的包围盒重叠做并查集分组：同一片的各级归一组，
            //   而文件里并排放的多片仍各自成组。
            var parent = Enumerable.Range(0, boxes.Count).ToArray();
            int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
            void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[rb] = ra; }
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    // ⚠⚠ **必须三个方向都判**（2026-08-17 实测修）。
                    //   原来只判 X/Z：意图是把「同一片的各级台阶」并成一组 ——
                    //   它们在 X/Z 上确实重叠。但**四片法兰沿 Y 排成一列时，
                    //   X/Z 也完全相同**（就是同一个轮廓平移），于是四片被并成一组，
                    //   射线一次穿透四片、厚度**相加**。
                    //   实测 Pt_Heater3.3dm：读出「3 级 4/8/12 mm、单片 5467 g」，
                    //   而真实是四片各 ~1.9/3.0 mm、单片 1368 g —— 差 4 倍，且不报错。
                    //   这与 3DM **写**那边「同层被一次穿透、厚度加起来」是同一个病，
                    //   写那边按片分层修好了，**读这边一直没修**。
                    //   ⇒ 同一片的各级共享同一个 Y 带（都含中面），不同片沿 Y 分离 ⇒ 加 Y 判据即可分开。
                    bool ox = boxes[i].Min.X <= boxes[j].Max.X && boxes[j].Min.X <= boxes[i].Max.X;
                    bool oz = boxes[i].Min.Z <= boxes[j].Max.Z && boxes[j].Min.Z <= boxes[i].Max.Z;
                    bool oy = boxes[i].Min.Y <= boxes[j].Max.Y && boxes[j].Min.Y <= boxes[i].Max.Y;
                    if (ox && oz && oy) Union(i, j);
                }
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < boxes.Count; i++)
            {
                int r = Find(i);
                if (!groups.TryGetValue(r, out var lst)) groups[r] = lst = new List<int>();
                lst.Add(i);
            }

            // 选组：给了 yPlane 就选中面最接近的那组，否则选实体最多（其次体积最大）的那组
            List<int> sel = groups.Values.First();
            if (!double.IsNaN(yPlane))
            {
                double best = double.MaxValue;
                foreach (var g in groups.Values)
                {
                    var gb = BoundingBox.Empty;
                    foreach (var i in g) gb.Union(boxes[i]);
                    double c = 0.5 * (gb.Min.Y + gb.Max.Y);
                    if (Math.Abs(c - yPlane) < best) { best = Math.Abs(c - yPlane); sel = g; }
                }
            }
            else
            {
                double bestVol = -1;
                foreach (var g in groups.Values)
                {
                    var gb = BoundingBox.Empty;
                    foreach (var i in g) gb.Union(boxes[i]);
                    double v = gb.Volume;
                    if (g.Count > sel.Count || (g.Count == sel.Count && v > bestVol))
                    { sel = g; bestVol = v; }
                }
            }

            var parts = sel.Select(i => breps[i]).ToList();
            var bb = BoundingBox.Empty;
            foreach (var i in sel) bb.Union(boxes[i]);
            Console.Error.WriteLine($"[thickness] 图层实体 {breps.Count} 个，分 {groups.Count} 组，" +
                                    $"本次量 {parts.Count} 个（同一片的各级）");
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
                    var ray = new LineCurve(new Line(new Point3d(x, yLo, z), new Point3d(x, yHi, z)));
                    double sum = 0;
                    foreach (var bp in parts)
                    {
                        if (!Rhino.Geometry.Intersect.Intersection.CurveBrep(
                                ray, bp, tol, out Curve[] _, out Point3d[] pts)
                            || pts == null || pts.Length < 2) continue;
                        var ys = pts.Select(q => q.Y).OrderBy(v => v).ToArray();
                        for (int k = 0; k + 1 < ys.Length; k += 2) sum += ys[k + 1] - ys[k];
                    }
                    if (sum > tol) { t[i * nz + j] = sum; solidPts++; }
                }
            }

            var outp = new Dictionary<string, object>
            {
                ["file"] = Path.GetFullPath(path),
                ["layer"] = layerName,
                ["planeY"] = 0.5 * (bb.Min.Y + bb.Max.Y),
                // ★ 分组信息必须进 JSON：调用方只在退出码非 0 时才读 stderr，
                //   而「一个图层里有好几片」恰恰是**能正常跑完**的那种错。
                ["groupCount"] = groups.Count,
                ["partsUsed"] = parts.Count,
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
    // （RunPlate 的 [MethodImpl(NoInlining)] 在它自己的定义前，见文件末尾；
    //   下面先插入 final 模式的实现。）

    // ========================================================================
    //  final：渲染设计记录整机几何
    //
    //  约定（与 thickness 模式的读取端、Pt_Heater.3dm、Core.FlangePlate 一致）：
    //    · 板面在 XZ 平面，厚度沿 Y；管轴 = Y
    //    · 板体关于自身中面对称（正负 t/2）—— 模型里焊角与加厚都是「两面各堆一半」
    //      （ShellThermal 的 weld 项写作 2*(...) 就是这个意思）
    //    （2026-08-12 曾把板画在 XY 面、沿 Z 拉伸，与读取端差 90 度，
    //      导致自己写出的 .3dm 再读回来量到 0 材料 —— round-trip 是硬性验收项）
    //
    //  分层：铂管 / 法兰-板身 / 法兰-环外级 / 法兰-环内级 / 法兰-角焊缝 / 压接段（参考）
    //  各级为独立实体（沿用 steps 模式的做法）：headless 下布尔并集不稳，
    //  而加工上本来就是「板身 + 两级台阶」，分开更贴近工艺。
    // ========================================================================
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunFinal(string specJson, string outPath)
    {
        using (var jd = JsonDocument.Parse(specJson))
        using (new RhinoCore(new[] { "/NOSPLASH" }, WindowStyle.Hidden))
        {
            var R = jd.RootElement;
            double D(string k) => R.GetProperty(k).GetDouble();
            string name = R.GetProperty("name").GetString() ?? "";
            double wall = D("wallMm"), tubeId = D("tubeIdMm"),
                   segLen = D("segLenMm"), discR = D("discR"), holeR = D("holeR"),
                   tabX = D("tabX"), tabHW = D("tabHW"), filletR = D("filletR"),
                   clampLen = D("clampLenMm");
            int segCount = R.GetProperty("segCount").GetInt32();
            var ringR = R.GetProperty("ringR").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            var plates = R.GetProperty("plates").EnumerateArray().ToArray();

            var doc = RhinoDoc.CreateHeadless(null);
            if (doc == null) { Console.Error.WriteLine("CreateHeadless 返回 null"); return 3; }
            doc.ModelUnitSystem = UnitSystem.Millimeters;
            double tol = doc.ModelAbsoluteTolerance;

            // 图层按**片**分，不按类型分。
            //   理由一：交付件要能单独调出某一片。
            //   理由二（更硬）：厚度探针沿 Y 打射线，而四片正是沿 Y 排成一列 ——
            //   同层会被一次穿透、厚度**加起来**（实测 10.450 = 2.11+3.40+3.18+1.76）。
            //   按片分层之后，round-trip 才量得到单片的真实厚度。
            int lyTube = doc.Layers.Add("铂管", System.Drawing.Color.Silver);
            if (lyTube < 0) lyTube = 0;
            int Ly(string nm, System.Drawing.Color c)
            { int k = doc.Layers.Add(nm, c); return k < 0 ? 0 : k; }

            // 板身轮廓：盘弧 + 舌根圆角 + 舌片直边
            //   圆角圆心 (xc, +-(w+r))，|中心| = R + r 故与盘圆外切；
            //   且中心 z = w + r 故与舌片直边 z = +-w 相切。凹角被这段弧填掉。
            Curve BodyOutline()
            {
                double w = tabHW, fr = Math.Max(0, filletR);
                var poly = new PolyCurve();
                if (fr > 1e-9 && (discR + fr) > (w + fr))
                {
                    double xc = -Math.Sqrt((discR + fr) * (discR + fr) - (w + fr) * (w + fr));
                    double k = discR / (discR + fr);
                    var tUp = new Point3d(xc * k, 0, (w + fr) * k);
                    var tDn = new Point3d(tUp.X, 0, -tUp.Z);
                    var fUp = new Point3d(xc, 0, w);
                    var fDn = new Point3d(xc, 0, -w);
                    var e1 = new Point3d(tabX, 0, w);
                    var e2 = new Point3d(tabX, 0, -w);
                    var cUp = new Point3d(xc, 0, w + fr);
                    var cDn = new Point3d(xc, 0, -(w + fr));
                    Point3d FilletMid(Point3d a, Point3d b, Point3d c)
                    {
                        var m = new Point3d((a.X + b.X) / 2, 0, (a.Z + b.Z) / 2);
                        var v = m - c; v.Unitize();
                        return c + v * fr;
                    }
                    poly.Append(new ArcCurve(new Arc(tDn, new Point3d(discR, 0, 0), tUp)));
                    poly.Append(new ArcCurve(new Arc(tUp, FilletMid(tUp, fUp, cUp), fUp)));
                    poly.Append(new LineCurve(fUp, e1));
                    poly.Append(new LineCurve(e1, e2));
                    poly.Append(new LineCurve(e2, fDn));
                    poly.Append(new ArcCurve(new Arc(fDn, FilletMid(fDn, tDn, cDn), tDn)));
                }
                else
                {
                    double amp = Math.Sqrt(tabX * tabX + w * w);
                    double phi = Math.Atan2(w, tabX);
                    double th = phi - Math.Acos(discR / amp);
                    var tp = new Point3d(discR * Math.Cos(th), 0, discR * Math.Sin(th));
                    var tn = new Point3d(tp.X, 0, -tp.Z);
                    poly.Append(new ArcCurve(new Arc(tn, new Point3d(discR, 0, 0), tp)));
                    poly.Append(new LineCurve(tp, new Point3d(tabX, 0, w)));
                    poly.Append(new LineCurve(new Point3d(tabX, 0, w), new Point3d(tabX, 0, -w)));
                    poly.Append(new LineCurve(new Point3d(tabX, 0, -w), tn));
                }
                poly.MakeClosed(tol);
                return poly;
            }

            Curve Circ(double r) => new Circle(Plane.WorldZX, Point3d.Origin, r).ToNurbsCurve();

            // 关于中面对称地拉伸：从 -t/2 拉到 +t/2，再整体平移到 y0
            // ── 造实体：一律用**曲线布尔**得到区域，再拉伸。
            //
            // ⚠ 不再依赖 CreatePlanarBreps 自己猜嵌套：它在内圈越出外轮廓时会判错，
            //   而我上一版为此加的「面积校验」假设内圈完全落在外轮廓内 ——
            //   板身的内圈是 r=31.8 的圆、越过盘缘，于是**把正确的件当错的挡掉了**，
            //   板身整块没写进文件（实测法兰只有计算值的 43%）。
            //   ⇒ 校验不能建立在一个比被验对象还窄的假设上。
            Brep Solid(Curve[] region, double t, double y0, string what)
            {
                var faces = Brep.CreatePlanarBreps(region, tol);
                if (faces == null || faces.Length == 0)
                { Console.Error.WriteLine("平面片创建失败：" + what); return null; }
                Brep merged = null;
                foreach (var f in faces)
                {
                    var sol = f.Faces[0].CreateExtrusion(
                        new LineCurve(Point3d.Origin, new Point3d(0, t, 0)), true);
                    if (sol == null) continue;
                    sol.Transform(Transform.Translation(0, y0 - t / 2, 0));
                    merged = merged == null ? sol : Brep.CreateBooleanUnion(
                        new[] { merged, sol }, tol)?.FirstOrDefault() ?? merged;
                }
                if (merged == null) Console.Error.WriteLine("拉伸失败：" + what);
                return merged;
            }

            // ★ 曲线布尔「相交却挖不出来」的记录 —— 非空 ⇒ 整张图判失败（见 RunFinal 末尾）
            var boolFailed = new List<string>();

            // 把闭合曲线裁到板身轮廓内（环按半径生效，会越过盘缘伸到轮廓外的空处）
            Curve[] ClipToBody(Curve c, Curve bodyOutline)
            {
                var r = Curve.CreateBooleanIntersection(c, bodyOutline, tol);
                return (r == null || r.Length == 0) ? new[] { c } : r;
            }
            // 区域 = 外圈（已裁）减内圈
            // ★★★★★ **「挖失败」不许当成「不用挖」**（2026-09-06 用户看图抓到）。
            //
            //   老写法：布尔差返回空 ⇒ `else acc.Add(o)` 把**没挖的原轮廓**原样放回去。
            //   本意是「内圈完全在外轮廓之外 ⇒ 本来就不用挖」，但它把**真失败**也吞了：
            //
            //     盘R35 < 舌半宽40 ⇒ BodyOutline 走错分支、算出**自交**的轮廓
            //       ⇒ CreateBooleanDifference 在自交曲线上失败、返回空
            //       ⇒ 兜底把没挖的实心轮廓写进 .3dm
            //       ⇒ **法兰实心穿过铂金管**（探针实测：板身最内半径 0.000 mm，
            //          5810 个采样点落在管外径 R26 以内），而**一句警告都没有**。
            //
            //   ⇒ 两种情况必须分开判，判据是闭式的、不用猜：
            //     · 内圈与外轮廓**根本不相交** ⇒ 真的不用挖，保留 o（合法）
            //     · 相交却挖不出来          ⇒ **几何有病**，当场喊出来并让整张图失败
            Curve[] RegionMinus(Curve[] outer, Curve inner)
            {
                var acc = new List<Curve>();
                foreach (var o in outer)
                {
                    var d = Curve.CreateBooleanDifference(o, inner, tol);
                    if (d != null && d.Length > 0) { acc.AddRange(d); continue; }

                    // 挖不出来 —— 先问「本来就不用挖」还是「挖失败」
                    var ix = Rhino.Geometry.Intersect.Intersection.CurveCurve(o, inner, tol, tol);
                    var bo = o.GetBoundingBox(true);
                    var bi = inner.GetBoundingBox(true);
                    bool overlap = bo.Min.X <= bi.Max.X && bi.Min.X <= bo.Max.X
                                && bo.Min.Z <= bi.Max.Z && bi.Min.Z <= bo.Max.Z;
                    bool touches = (ix != null && ix.Count > 0) || overlap;
                    if (!touches) { acc.Add(o); continue; }      // 真的不用挖

                    boolFailed.Add($"内圈与外轮廓相交（交点 {(ix?.Count ?? 0)} 个、包围盒重叠 {overlap}）"
                                 + $"，但布尔差挖不出来 —— 轮廓多半自交/不闭合"
                                 + $"（外轮廓闭合 {o.IsClosed}、内圈闭合 {inner.IsClosed}）");
                    acc.Add(o);   // 仍放回去，好让人打开文件看见病灶；但整张图会被判失败
                }
                return acc.ToArray();
            }

            // ── 角焊缝：与 Core/PlateCurrent2D.ThicknessAt 同一式子
            //   weld(d) = 2(a − √(a²−(d−a)²))，d = r − 孔R，0 ≤ d < a，a = max(板厚, 壁厚)
            //   半边 hw(d) = a − √(a²−(d−a)²)。写成隐式就看得清它**是什么**：
            //       (d − a)² + (hw − a)² = a²
            //   ⇒ 板面之上是一段**半径 a 的圆弧**，圆心 (孔R+a, 板面+a)；
            //     d=0 处切线竖直（贴管壁），d=a 处切线水平（贴板面）—— 标准凹角焊缝。
            //
            // ⚠ 上一版拿 12 段同心带逼近这段弧：体积对得上（差 0.7%），但**形状是错的** ——
            //   Rhino 里看到的是一圈阶梯，不是焊缝（2026-08-16 用户实测发现）。
            //   体积对账通过 ≠ 几何正确：对账只约束一个标量，形状有无穷多自由度。
            //   ⇒ 改成把这段弧**绕 Y 轴回转**。既是真圆弧，体积也从近似变成精确。
            // ⚠ 边界必须写 `d < 0`，**不能**写 `d <= 0`：d=0（正贴管壁）处 hw = a，
            //   那是整条弧的**最高点**。写成 d<=0 会把它压到 0，弧退化成一条浅拱：
            //   实测焊缝高度只剩 0.283 而不是 2.11（= a(1−√3/2)，三点定弧的中点值），
            //   体积随之只有 43%。与 Core/PlateCurrent2D 的 `d >= 0 && d < a` 逐字对齐。
            double Hw(double d, double a2) =>
                (a2 <= 1e-9 || d < 0 || d >= a2) ? 0
                : a2 - Math.Sqrt(Math.Max(0, a2 * a2 - (d - a2) * (d - a2)));

            // 一段焊肉：d∈[d0,d1]、坐在 yBase 这个板面上、sg=+1 上面 / −1 下面。
            // 剖面 = 底边(贴板面) + 右竖边 + 圆弧 + 左竖边(贴管壁)，整圈绕 Y 轴回转。
            Brep WeldBead(double d0, double d1, double a2, double yBase, int sg)
            {
                if (a2 <= 1e-9 || d1 <= d0 + 1e-9) return null;
                Point3d Top(double d) => new(holeR + d, yBase + sg * Hw(d, a2), 0);
                Point3d Bot(double d) => new(holeR + d, yBase, 0);

                var arc = new Arc(Top(d1), Top((d0 + d1) / 2), Top(d0));
                if (!arc.IsValid) { Console.Error.WriteLine("焊缝圆弧无效"); return null; }

                // ⚠ 不要把整条闭合 PolyCurve 交给 RevSurface.Create：它只回转出**一张**
                //   带拐点的面，Brep.CreateFromRevSurface 得到的壳不闭合，
                //   VolumeMassProperties 于是给 0（第一版实测四片全是 0 mm³）。
                //   ⇒ 逐段回转成面，再 JoinBreps 缝成闭合体。
                var segs = new List<Curve> { new LineCurve(Bot(d0), Bot(d1)) };
                if (Hw(d1, a2) > 1e-9) segs.Add(new LineCurve(Bot(d1), Top(d1)));
                segs.Add(new ArcCurve(arc));
                if (Hw(d0, a2) > 1e-9) segs.Add(new LineCurve(Top(d0), Bot(d0)));

                var axis = new Line(Point3d.Origin, new Point3d(0, 1, 0));
                var faces = new List<Brep>();
                foreach (var sc in segs)
                {
                    var rev = RevSurface.Create(sc, axis);
                    // 剖面最内也在 r=孔R，**不碰转轴** ⇒ 整圈回转自身即闭合，无需封盖
                    var fb = rev == null ? null : Brep.CreateFromRevSurface(rev, false, false);
                    if (fb != null) faces.Add(fb);
                }
                var joined = Brep.JoinBreps(faces, tol);
                var b = joined?.FirstOrDefault(x => x.IsSolid) ?? joined?.FirstOrDefault();
                if (b == null) { Console.Error.WriteLine("焊缝缝合失败"); return null; }
                if (!b.IsSolid)
                { Console.Error.WriteLine($"焊缝非闭合体（面 {faces.Count}，缝出 {joined.Length}）"); return null; }
                return b;
            }
            int made = 0;
            void Add(Brep b, int layer, string nm)
            {
                if (b == null) { Console.Error.WriteLine("实体创建失败：" + nm); return; }
                var att = new Rhino.DocObjects.ObjectAttributes { LayerIndex = layer, Name = nm };
                if (doc.Objects.AddBrep(b, att) != Guid.Empty) made++;
            }

            var body = BodyOutline();
            if (!body.IsClosed) { Console.Error.WriteLine("板身轮廓未闭合"); return 6; }
            // ★ 轮廓自交 = 后面每一次曲线布尔都会静默失败 ⇒ 当场拦，别写出一张坏图
            {
                var self = Rhino.Geometry.Intersect.Intersection.CurveSelf(body, tol);
                if (self != null && self.Count > 0)
                {
                    Console.Error.WriteLine($"板身轮廓**自交** {self.Count} 处 —— "
                        + $"盘半径 {discR:0.0} vs 舌半宽 {tabHW:0.0}："
                        + (discR < tabHW ? "**盘比舌还窄**，BodyOutline 的分支假设不成立"
                                         : "轮廓参数异常")
                        + "。继续画下去，管孔与台阶的布尔差都会静默失败（实测：法兰实心穿过铂金管）。");
                    return 7;
                }
            }

            // ══ 槽与孔的裁剪曲线 ═══════════════════════════════════════════════
            //  ★ 形状必须与 Core/PlateCurrent2D 的 DiscSlot/TabHole.Contains **逐字对应** ——
            //    求解器是按那两个判据算的裕度，画成别的形状就是「算一个、画另一个」。
            //
            //  弯椭圆槽 = 到中弧（半径 rm、张角 span）的距离 ≤ 半宽 hw
            //    ⇒ 边界 = 外弧(rm+hw) + 端半圆(hw) + 内弧(rm−hw) + 端半圆(hw)
            //    两端是半圆而不是尖角：尖角在电流场里是尖点，在加工上也是裂纹源。
            Curve SlotCurve(double rin, double rout, double centerDeg, double spanDeg)
            {
                double rm = 0.5 * (rin + rout), hw = 0.5 * (rout - rin);
                if (hw <= 1e-9 || spanDeg <= 0.5) return null;
                double c = centerDeg * Math.PI / 180.0, half = spanDeg * 0.5 * Math.PI / 180.0;
                Point3d P(double r, double th) => new(r * Math.Cos(th), 0, r * Math.Sin(th));
                // 端帽的最外点：沿中弧切向再走 hw
                Point3d Cap(double th, int sg) =>
                    new(rm * Math.Cos(th) - sg * hw * Math.Sin(th), 0,
                        rm * Math.Sin(th) + sg * hw * Math.Cos(th));
                double a0 = c - half, a1 = c + half;
                var pc = new PolyCurve();
                pc.Append(new ArcCurve(new Arc(P(rm + hw, a0), P(rm + hw, c), P(rm + hw, a1))));
                pc.Append(new ArcCurve(new Arc(P(rm + hw, a1), Cap(a1, +1), P(rm - hw, a1))));
                pc.Append(new ArcCurve(new Arc(P(rm - hw, a1), P(rm - hw, c), P(rm - hw, a0))));
                pc.Append(new ArcCurve(new Arc(P(rm - hw, a0), Cap(a0, -1), P(rm + hw, a0))));
                pc.MakeClosed(tol);
                return pc.IsClosed ? pc : null;
            }

            //  舌板孔 = 椭圆，长轴**顺流**（沿 X）。AspectXZ = 长/短，1 = 正圆。
            //    与 TabHole.Contains 的 (dx/(R·asp))² + (dz/R)² ≤ 1 一致。
            Curve HoleCurve(double cx, double r, double asp)
            {
                if (r <= 0.05) return null;
                var pl = new Plane(new Point3d(cx, 0, 0),
                                   new Vector3d(1, 0, 0), new Vector3d(0, 0, 1));
                return new Ellipse(pl, r * Math.Max(asp, 1e-6), r).ToNurbsCurve();
            }

            //  逐条从区域里减掉。**三级板身都要减** —— 槽带可能压在环上，
            //  而 Inside() 是「槽里就没有料」，不分级。
            Curve[] CutAll(Curve[] region, List<Curve> cutters)
            {
                var acc = region;
                foreach (var cu in cutters) acc = RegionMinus(acc, cu);
                return acc;
            }
            var weldChk = new List<object>();

            for (int j = 0; j < plates.Length; j++)
            {
                var P = plates[j];
                string pn = P.GetProperty("name").GetString() ?? ("片" + (j + 1));
                double t = P.GetProperty("t").GetDouble();
                var ring = P.GetProperty("ring").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                double y0 = j * segLen;

                // 板身挖到环的外边界，内侧由两级环补齐，避免面重合
                int lyBody  = Ly(pn + "-板身",   System.Drawing.Color.Gold);
                int lyRingO = Ly(pn + "-环外级", System.Drawing.Color.Orange);
                int lyRingI = Ly(pn + "-环内级", System.Drawing.Color.OrangeRed);
                int lyClamp = Ly(pn + "-压接段", System.Drawing.Color.DarkCyan);
                // ★ 本片的槽与孔（spec 里没有这几项 = 老档，按「不开」处理，行为与从前逐位相同）
                double PD(string k, double dflt) =>
                    P.TryGetProperty(k, out var v) ? v.GetDouble() : dflt;
                var cutters = new List<Curve>();
                var sc = SlotCurve(PD("slotRIn", 0), PD("slotROut", 0), 0, PD("slotDeg", 0));
                if (sc != null) cutters.Add(sc);
                var hc = HoleCurve(PD("holeX", double.NaN), PD("holeR", 0), PD("holeAsp", 1));
                if (hc != null && !double.IsNaN(PD("holeX", double.NaN))) cutters.Add(hc);
                if (cutters.Count > 0)
                    Console.Error.WriteLine($"[final] {pn}：切 {cutters.Count} 个"
                        + $"（槽 {PD("slotDeg", 0):0}° r{PD("slotRIn", 0):0.0}-{PD("slotROut", 0):0.0}"
                        + $"，孔 R{PD("holeR", 0):0.0}×{PD("holeAsp", 1):0.0} @x{PD("holeX", 0):0.0}）");

                // 板身 = 轮廓 − 环外边界（环外边界可能越过盘缘，故用曲线布尔差）− 槽/孔
                Add(Solid(CutAll(RegionMinus(new[] { body }, Circ(ringR[1])), cutters), t, y0, pn + "板身"),
                    lyBody, pn + "_板身_t" + t.ToString("0.00"));
                // 环：外圈**裁到轮廓内**，否则盘缘之外会凭空长出一整圈料
                Add(Solid(CutAll(RegionMinus(ClipToBody(Circ(ringR[1]), body), Circ(ringR[0])), cutters), ring[1], y0, pn + "环外级"),
                    lyRingO, pn + "_环外级_r" + ringR[0].ToString("0.0") + "-" + ringR[1].ToString("0.0")
                       + "_t" + ring[1].ToString("0.00"));
                Add(Solid(CutAll(RegionMinus(ClipToBody(Circ(ringR[0]), body), Circ(holeR)), cutters), ring[0], y0, pn + "环内级"),
                    lyRingI, pn + "_环内级_r" + holeR.ToString("0.0") + "-" + ringR[0].ToString("0.0")
                       + "_t" + ring[0].ToString("0.00"));

                // 角焊缝：焊脚 a 可能**跨过台阶边界**（a 最大 3.40，而环内级只有 3 mm 宽），
                // 故按分区切段，每段坐在**自己那一级的板面**上 —— 否则跨界那一小片焊肉会
                // 悬在半空（FE 里焊缝是叠加在**当地**分区厚度之上的，不是叠在同一个面上）。
                double aw = Math.Max(t, wall);
                int lyWeld = Ly(pn + "-角焊缝", System.Drawing.Color.Crimson);
                if (holeR + aw > discR + 1e-9)
                    Console.Error.WriteLine($"焊脚越出盘缘：{pn} 孔R+a={holeR + aw:0.00} > 盘R={discR:0.00}");
                var zoneR = new[] { ringR[0], ringR[1], double.PositiveInfinity };
                var zoneT = new[] { ring[0], ring[1], t };
                double dPrev = 0, weldDrawn = 0;
                for (int k = 0; k < zoneR.Length && dPrev < aw - 1e-9; k++)
                {
                    double dNext = Math.Min(aw, zoneR[k] - holeR);
                    if (dNext <= dPrev + 1e-9) continue;
                    foreach (int sg in new[] { +1, -1 })
                    {
                        var bead = WeldBead(dPrev, dNext, aw, y0 + sg * zoneT[k] / 2, sg);
                        var bmp = bead == null ? null : VolumeMassProperties.Compute(bead);
                        if (bmp != null) weldDrawn += bmp.Volume;
                        Add(bead, lyWeld,
                            pn + $"_角焊缝_a{aw:0.00}_r{holeR + dPrev:0.00}-{holeR + dNext:0.00}_"
                               + (sg > 0 ? "上" : "下"));
                    }
                    dPrev = dNext;
                }

                // ★ 交叉核对：回转体的体积 vs **解析积分**（与 FE 同一被积函数），
                //   两条互不相干的路算同一个量。上一版正是缺这一步，才让「阶梯」蒙混过去。
                //   换元 d − a = −a·cosθ（θ:0→π/2）去掉 d=0 处的竖直切线，被积函数才光滑。
                double weldExact = 0;
                {
                    const int N = 4000;
                    for (int k = 0; k < N; k++)
                    {
                        double th2 = Math.PI / 2 * (k + 0.5) / N;
                        double dd = aw * (1 - Math.Cos(th2));
                        weldExact += 2 * aw * (1 - Math.Sin(th2))          // weld(d) = 2·hw
                                   * 2 * Math.PI * (holeR + dd)            // 环周长
                                   * aw * Math.Sin(th2) * (Math.PI / 2 / N); // dd/dθ·dθ
                    }
                }
                weldChk.Add(new
                {
                    plate = pn,
                    a = Math.Round(aw, 3),
                    drawnMm3 = Math.Round(weldDrawn, 3),
                    exactMm3 = Math.Round(weldExact, 3),
                    relErr = Math.Round((weldDrawn - weldExact) / Math.Max(1e-9, weldExact), 5)
                });

                // 压接段（参考几何，非铂件）：自舌端往回 clampLen
                var cl = new PolyCurve();
                var a1 = new Point3d(tabX, 0, tabHW);
                var a2 = new Point3d(tabX + clampLen, 0, tabHW);
                var a3 = new Point3d(tabX + clampLen, 0, -tabHW);
                var a4 = new Point3d(tabX, 0, -tabHW);
                cl.Append(new LineCurve(a1, a2));
                cl.Append(new LineCurve(a2, a3));
                cl.Append(new LineCurve(a3, a4));
                cl.Append(new LineCurve(a4, a1));
                cl.MakeClosed(tol);
                // ⚠ 压接段只是**标出铜排夹在哪**，不是一块料。
                //   第一版把它拉伸成实体，与舌片同位同厚 ⇒ 两块料占同一处空间（用户实测发现）。
                //   ⇒ 改成画在板面上的**闭合曲线**，不增加任何体积。
                cl.Translate(new Vector3d(0, y0 + t / 2, 0));
                var attC = new Rhino.DocObjects.ObjectAttributes { LayerIndex = lyClamp };
                attC.Name = pn + "_压接段" + clampLen.ToString("0") + "mm_参考线";
                if (doc.Objects.AddCurve(cl, attC) != Guid.Empty) made++;
            }

            double ri = tubeId / 2.0, ro = ri + wall;
            for (int i = 0; i < segCount; i++)
            {
                var solid = Solid(RegionMinus(new[] { Circ(ro) }, Circ(ri)), segLen,
                                  i * segLen + segLen / 2, "管段" + (i + 1));
                if (solid == null) { Console.Error.WriteLine("管截面创建失败"); return 7; }
                Add(solid, lyTube, "管段" + (i + 1) + "_D" + tubeId.ToString("0") + "x"
                    + wall.ToString("0.00") + "_L" + segLen.ToString("0"));
            }

            if (!doc.WriteFile(outPath, new Rhino.FileIO.FileWriteOptions { FileVersion = 7 }))
            { Console.Error.WriteLine("写文件失败：" + outPath); return 9; }

            // ── round-trip：**从磁盘读回**，逐实体量包围盒
            //
            // 为什么是包围盒的 Y 向跨度：本模式一律「板面在 XZ、沿 Y 拉伸」，
            // 故 Y 跨度就是板厚（**角焊缝除外** —— 它是回转体，Y 跨度是焊脚高 a）。
            // 若哪天又把板画到 XY 面沿 Z 拉伸（2026-08-12 出过），
            // Y 跨度会变成**盘直径**而不是板厚 —— 一眼就露馅，不必等下游量厚度。
            //
            // ⚠ 必须**读回文件**而不是量内存里的 doc：要验的正是「写出去再读回来」这一段。
            var rt = new List<object>();
            using (var f3 = Rhino.FileIO.File3dm.Read(outPath))
            {
                if (f3 == null) { Console.Error.WriteLine("回读失败：" + outPath); return 10; }
                foreach (var ob in f3.Objects)
                {
                    var g = ob.Geometry;
                    if (g == null) continue;
                    var bb = g.GetBoundingBox(true);
                    if (!bb.IsValid) continue;
                    // File3dmLayerTable 不支持 [] 索引（照 WinFormRhino8App 的做法按 Index 找）
                    var lyObj = f3.AllLayers.FirstOrDefault(l => l.Index == ob.Attributes.LayerIndex);
                    string ly = lyObj?.Name ?? "?";
                    // ★ 必须报**体积**。原来只报包围盒，而包围盒**分辨不出中间有没有挖孔** ——
                    //   实心圆盘与圆环的厚度、外廓完全一样，校验会一路报「全吻合」而放过它。
                    //   （2026-08-16 用户在 Rhino 里打开才发现板身盖住了管口。）
                    double vol = double.NaN; bool solid = false;
                    var brep = g as Brep ?? (g as Extrusion)?.ToBrep();
                    if (brep != null)
                    {
                        solid = brep.IsSolid;
                        var mp = VolumeMassProperties.Compute(brep);
                        if (mp != null) vol = mp.Volume;
                    }
                    rt.Add(new
                    {
                        layer = ly,
                        obj = ob.Attributes.Name ?? "",
                        tY = Math.Round(bb.Max.Y - bb.Min.Y, 4),   // 沿 Y = 厚度（管段则是段长）
                        y0 = Math.Round((bb.Max.Y + bb.Min.Y) / 2, 4),
                        dX = Math.Round(bb.Max.X - bb.Min.X, 3),
                        dZ = Math.Round(bb.Max.Z - bb.Min.Z, 3),
                        vol = double.IsNaN(vol) ? (object)null : Math.Round(vol, 2),
                        solid
                    });
                }
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                file = outPath, name, solids = made,
                wall, tubeId, segLen, segCount, discR, holeR, tabX, tabHW, filletR,
                ringR, plateCount = plates.Length,
                weldCheck = weldChk,
                roundTrip = rt
            }));

            // ★★★★★ 曲线布尔「相交却挖不出来」⇒ **整张图判失败**（2026-09-06）。
            //   写在最后而不是当场 return：文件已经写出来了，让人能打开看见病灶，
            //   但返回码非 0 ⇒ 调用方（Geometry3dm.WriteFinal3dm）会抛，
            //   **绝不会有人拿着这张图当成功的交付件**。
            if (boolFailed.Count > 0)
            {
                Console.Error.WriteLine($"曲线布尔失败 {boolFailed.Count} 处 —— 这张图**不可用**：");
                foreach (var w in boolFailed.Distinct().Take(8)) Console.Error.WriteLine("  · " + w);
                Console.Error.WriteLine("  典型后果：管孔没挖成，法兰实心穿过铂金管。");
                return 8;
            }
            return 0;
        }
    }

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


    /// <summary>
    /// 阶梯法兰：各级为**独立实体**的同心环 + 一片舌片，可选径向开槽。
    /// 板面在 XZ、厚度沿 Y，与 thickness 读取端一致。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunSteps(string outPath, double holeR, double[] rs, double[] ts,
                                double tabX, double tabHW, double tabT,
                                int slotN, double slotDeg, double slotRin, double slotRout,
                                bool tabParallel = false,
                                System.Collections.Generic.List<(double X, double Z, double R)>? tabHolesIn = null)
    {
        var tabHoles = tabHolesIn ?? new System.Collections.Generic.List<(double X, double Z, double R)>();
        using (new RhinoCore(new[] { "/NOSPLASH" }, WindowStyle.Hidden))
        {
            var doc = RhinoDoc.CreateHeadless(null);
            double tol = doc.ModelAbsoluteTolerance;
            int layer = doc.Layers.Add("法兰", System.Drawing.Color.Green);
            var pl = Plane.WorldZX;              // 法向 = +Y

            Brep? MakeRing(double rin, double rout, double t)
            {
                var co = new Circle(pl, Point3d.Origin, rout).ToNurbsCurve();
                var ci = new Circle(pl, Point3d.Origin, rin).ToNurbsCurve();
                var fs = Brep.CreatePlanarBreps(new Curve[] { co, ci }, tol);
                if (fs == null || fs.Length == 0) return null;
                return fs[0].Faces[0].CreateExtrusion(
                    new LineCurve(Point3d.Origin, new Point3d(0, t, 0)), true);
            }

            int made = 0;
            for (int i = 0; i < rs.Length; i++)
            {
                double rin = i == 0 ? holeR : rs[i - 1];
                var ring = MakeRing(rin, rs[i], ts[i]);
                if (ring == null) { Console.Error.WriteLine("环创建失败 级" + (i + 1)); return 5; }

                if (slotN > 0 && i == 0)
                {
                    for (int k = 0; k < slotN; k++)
                    {
                        double a0 = 2 * Math.PI * k / slotN - slotDeg * Math.PI / 360;
                        double a1 = a0 + slotDeg * Math.PI / 180;
                        var pts = new List<Point3d>();
                        for (int q = 0; q <= 8; q++)
                        {
                            double aa = a0 + (a1 - a0) * q / 8.0;
                            pts.Add(new Point3d(slotRout * Math.Cos(aa), 0, slotRout * Math.Sin(aa)));
                        }
                        for (int q = 8; q >= 0; q--)
                        {
                            double aa = a0 + (a1 - a0) * q / 8.0;
                            pts.Add(new Point3d(slotRin * Math.Cos(aa), 0, slotRin * Math.Sin(aa)));
                        }
                        pts.Add(pts[0]);
                        var wedge = new PolylineCurve(pts);
                        var wf = Brep.CreatePlanarBreps(new Curve[] { wedge }, tol);
                        if (wf == null || wf.Length == 0) continue;
                        var cut = wf[0].Faces[0].CreateExtrusion(
                            new LineCurve(new Point3d(0, -1, 0), new Point3d(0, ts[i] + 1, 0)), true);
                        if (cut == null) continue;
                        var diff = Brep.CreateBooleanDifference(new[] { ring }, new[] { cut }, tol);
                        if (diff != null && diff.Length > 0) ring = diff[0];
                    }
                }

                var att = new Rhino.DocObjects.ObjectAttributes { LayerIndex = layer };
                att.Name = "级" + (i + 1);
                if (doc.Objects.AddBrep(ring, att) != Guid.Empty) made++;
            }

            double rout2 = rs[rs.Length - 1];
            double amp = Math.Sqrt(tabX * tabX + tabHW * tabHW);
            if (rout2 < amp)
            {
                // ★★★★★ 舌型必须与 FlangePlate 那一侧对得上（2026-08-24）。
                //
                // 本模式原来只会写**梯形**舌（从圆上的切点收到末端半宽）。可本项目的
                // 设计记录几何早就改成**等宽舌**（FlangePlate.TabParallel）——理由写在那里：
                // 梯形在两条约束上同时吃亏（导热漏按平均截面、局部失稳按最窄截面）。
                // 于是「写出来的图」与「APP 在设计的形状」是两族：
                // 实测把梯形舌那张图读回来，等宽替身面积差 −12.2 %、ShapeJ 差 8.2 %
                // ⇒ 保真门当场拒绝，而它拒绝得**对** —— 错的是写入器。
                //
                // ⇒ 等宽时舌片是一条 [tabX, x交] × [−w, +w] 的直条，
                //   x交 = −√(R²−w²) 正是 FlangePlate 的交界公式，两边同一个口径。
                Point3d tp, tn;
                if (tabParallel)
                {
                    double w = Math.Min(tabHW, rout2);
                    double xi = -Math.Sqrt(Math.Max(0, rout2 * rout2 - w * w));
                    tp = new Point3d(xi, 0, w);
                    tn = new Point3d(xi, 0, -w);
                }
                else
                {
                    double phi = Math.Atan2(tabHW, tabX);
                    double th = phi - Math.Acos(rout2 / amp);
                    tp = new Point3d(rout2 * Math.Cos(th), 0, rout2 * Math.Sin(th));
                    tn = new Point3d(tp.X, 0, -tp.Z);
                }
                var poly = new PolyCurve();
                // ⚠ 圆弧必须走**舌片这一侧**（过 −R）：舌片实体是「矩形**减去**圆盘」，
                //   靠这段弧把圆盘那块挖掉。改成过 +R 会把整个圆盘也圈进舌片，
                //   与三个环**叠加** ⇒ 读回来厚度变成 4/5/6、管孔被填掉（实测踩过）。
                poly.Append(new ArcCurve(new Arc(tp, new Point3d(-rout2, 0, 0), tn)));
                poly.Append(new LineCurve(tn, new Point3d(tabX, 0, -tabHW)));
                poly.Append(new LineCurve(new Point3d(tabX, 0, -tabHW), new Point3d(tabX, 0, tabHW)));
                poly.Append(new LineCurve(new Point3d(tabX, 0, tabHW), tp));
                poly.MakeClosed(tol);
                var tf2 = Brep.CreatePlanarBreps(new Curve[] { poly }, tol);
                if (tf2 != null && tf2.Length > 0)
                {
                    var tab = tf2[0].Faces[0].CreateExtrusion(
                        new LineCurve(Point3d.Origin, new Point3d(0, tabT, 0)), true);
                    // ★★★★★ **舌板开孔**（2026-09-05 用户要求）。
                    //   与圆盘开槽走**同一条路**：造截面 → 拉成柱 → BooleanDifference 挖掉。
                    //   ⚠ 柱要比板厚长一截（−1 … tabT+1），否则布尔差在两个面上共面，
                    //     Rhino 会给出不封闭的结果 —— 圆盘开槽那段已经这么干了。
                    if (tab != null && tabHoles.Count > 0)
                    {
                        var cutters = new System.Collections.Generic.List<Brep>();
                        foreach (var (hx, hz, hr) in tabHoles)
                        {
                            if (!(hr > 1e-6)) continue;
                            // ★★★ 圆心要**沉到板下面**（y = −1），不能放在 y = 0。
                            //   放 y = 0 时切柱底面与舌片底面**共面**，布尔差在共面处
                            //   失效 —— 而且 Rhino **不报错**：diff 返回非空、实体数照样是 4，
                            //   写出来的图上却没有孔。2026-09-05 回读门抓到的就是这个
                            //   （孔心仍有 1.800 mm 材料）。
                            var cc = new Circle(pl, new Point3d(hx, -1.0, hz), hr).ToNurbsCurve();
                            var cf = Brep.CreatePlanarBreps(new Curve[] { cc }, tol);
                            if (cf == null || cf.Length == 0) continue;
                            var cyl = cf[0].Faces[0].CreateExtrusion(
                                new LineCurve(Point3d.Origin, new Point3d(0, tabT + 2, 0)), true);
                            if (cyl != null) cutters.Add(cyl);
                        }
                        if (cutters.Count > 0)
                        {
                            // ★★★★★ **法向朝内的实体，布尔差会原样退回且不报错**（2026-09-05 实测）。
                            //   CreateExtrusion 造出来的实体是 Inward。不摆正就切：
                            //   CreateBooleanDifference 返回 1 个 brep、面数**仍是 6**（该有 9），
                            //   `实体数 4` 照常打印，**一句警告都没有** —— 写出的图上没有孔，
                            //   而计算里有孔 ⇒ 工程师拿到一张与计算不符的图。
                            //   是回读门（TabHoleTests.孔真的写进了图也读得回来）抓到的：孔心仍有 1.8 mm 材料。
                            //   ⚠ 圆盘开槽那段是同样的写法 —— 它的唯一调用方一直传 slotCount:0，
                            //     所以这个雷在那边**从没响过**。现在一并摆正。
                            if (tab.SolidOrientation == BrepSolidOrientation.Inward) tab.Flip();
                            foreach (var cq in cutters)
                                if (cq.SolidOrientation == BrepSolidOrientation.Inward) cq.Flip();
                            var diff = Brep.CreateBooleanDifference(new[] { tab }, cutters, tol);
                            if (diff != null && diff.Length > 0) tab = diff[0];
                            else Console.Error.WriteLine("⚠ 舌孔布尔差失败，写出的是**没有孔**的舌片");
                        }
                    }
                    if (tab != null)
                    {
                        var att2 = new Rhino.DocObjects.ObjectAttributes { LayerIndex = layer };
                        att2.Name = "舌片";
                        if (doc.Objects.AddBrep(tab, att2) != Guid.Empty) made++;
                    }
                }
            }

            if (!doc.WriteFile(outPath, new Rhino.FileIO.FileWriteOptions { FileVersion = 7 }))
            { Console.Error.WriteLine("写文件失败：" + outPath); return 9; }
            Console.WriteLine("实体数 " + made + " -> " + outPath);
            return 0;
        }
    }

}
