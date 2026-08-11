using System.Linq;
using PtOptimize.Core;
using PtOptimize.UI;

namespace PtOptimize;

internal static class Program
{
    /// <summary>
    /// 管根温差（控温点 − 法兰处管温）的**设计靶值** K —— HANDOVER §4.2k。
    ///
    /// 此前所有扫描都二分到 **0**，即 Φ = 1。那是把设计点放在悬崖边：
    /// 温差为正 = 法兰比管冷（安全，只是有冷点）；为负 = 法兰比管热 ⇒ 热量倒灌 ⇒ **烧断**。
    /// 二分到 0 意味着任何一点制造偏差、任何一点工况漂移都可能落到负的那一侧。
    ///
    /// 正确的靶是 <c>0 &lt; ΔT ≤ 10</c>，本常数取 5 K：
    /// 温差对法兰厚的斜率约 262 K/mm ⇒ 5 K ≈ **0.02 mm** 的厚度裕度，
    /// 而 §4.2c 已算出守住 ±10 K 需要 ±0.04 mm 公差 —— 两者同量级，5 K 是能守住的最大裕度。
    /// </summary>
    private const double RootDeltaTargetK = 5.0;

    /// <summary>
    /// 管根温差的判定（§4.2k，**单边**）：必须落在 (0, 10] 内。
    /// 旧代码写 <c>Math.Abs(d) ≤ 10</c>，于是 −8 K（法兰比管热 8 K，正走向烧断）判「✓」。
    /// </summary>
    private static bool RootDeltaOk(double dK, double maxK = 10.0) => dK > 0 && dK <= maxK;

    /// <summary>在单调序列上线性插值求 y = target 对应的 x</summary>
    private static double Interp(List<double> xs, List<double> ys, double target)
    {
        for (int i = 0; i < xs.Count - 1; i++)
        {
            double a = ys[i] - target, b = ys[i + 1] - target;
            if (a == 0) return xs[i];
            if (a * b < 0) return xs[i] + (xs[i + 1] - xs[i]) * a / (a - b);
        }
        return double.NaN;
    }

    /// <summary>
    /// 导出该算例的二维场图：法兰平面的**温度**与**电流密度**，外加铂金管的轴向温度剖面。
    ///
    /// 法兰场取自 PlateThermal2D / PlateCurrent2D（真二维）；
    /// **不用** FieldMap 的子午面图，其法兰部分走已作废的一维环形模型（HANDOVER §5）。
    /// 管子在模型里是一维（轴向），故以剖面图呈现，与法兰热图并列。
    ///
    /// 文件名：<tag>_T.png / <tag>_J.png / <tag>_tube.png，落在 figs/ 下。
    /// </summary>
    private static void SaveFields(DesignInputs p, CoupledResult c, string tag, string dir = "figs")
    {
        try
        {
            Directory.CreateDirectory(dir);
            ApplicationConfiguration.Initialize();

            void Save(string suffix, Action<ScottPlot.WinForms.FormsPlot> draw)
            {
                var fp = UI.FieldPlots.NewPlot();
                draw(fp);
                fp.Plot.SavePng(Path.Combine(dir, $"{tag}_{suffix}.png"), 1100, 620);
            }

            var th = c.Flange; var cur = c.Current;
            if (th.T.Length > 0 && cur.Mask.Length > 0)
                Save("T", f => UI.FieldPlots.DrawPlate(f, th.T, cur.Mask, cur.X0, cur.Z0, cur.H,
                    $"法兰平面 温度场  [{tag}]", "温度", "°C", p.TubeIdMm * 0.5 + p.WallMinMm));

            if (cur.Jmag.Length > 0)
                Save("J", f => UI.FieldPlots.DrawPlate(f, cur.Jmag, cur.Mask, cur.X0, cur.Z0, cur.H,
                    $"法兰平面 电流密度  [{tag}]", "J", "A/mm²", p.TubeIdMm * 0.5 + p.WallMinMm));

            if (c.Tube.Ok && c.Tube.X.Length > 0)
                Save("tube", f => UI.FieldPlots.DrawAxialProfile(f, c.Tube, p));

            Console.WriteLine($"    → 场图 {dir}/{tag}_T.png, _J.png, _tube.png");
        }
        catch (Exception ex) { Console.WriteLine($"    ⚠ 场图导出失败：{ex.Message}"); }
    }

    /// <summary>
    /// 同步进度回调。控制台没有同步上下文，<see cref="Progress{T}"/> 会把回调抛到线程池，
    /// 与主线程的 Console.WriteLine 交错成乱序 —— CLI 一律用这个。
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _h;
        public SyncProgress(Action<T> h) => _h = h;
        public void Report(T value) => _h(value);
    }

    /// <summary>从当前目录与 bin 目录逐级上溯找数据文件（bin\Debug\net8.0-windows 距仓库根三层）</summary>
    private static string Find3dm(string name)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                string c = Path.Combine(d.FullName, name);
                if (File.Exists(c)) return c;
            }
        return name;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // 批处理: Pt_Optimize.exe --cli [case.json]
        if (args.Length > 0 && args[0] == "--cli")
        {
            var p = args.Length > 1 && !args[1].StartsWith("--")
                ? System.Text.Json.JsonSerializer.Deserialize<DesignInputs>(File.ReadAllText(args[1]))!
                : new DesignInputs();
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* WinExe 无控制台 */ }

            // --cli --geom [file.3dm]   直接读 .3dm，校核代码里手抄的几何常数（需装 Rhino 8）
            // 放在求解之前：几何校核与热解无关，不必先花时间解一遍管段
            if (args.Contains("--geom"))
            {
                int gi = Array.IndexOf(args, "--geom");
                string f3dm = gi + 1 < args.Length && !args[gi + 1].StartsWith("--")
                              ? args[gi + 1] : Find3dm("Pt_Heater.3dm");
                try
                {
                    Console.WriteLine(Geometry3dm.Report(f3dm, p, new FlangePlate()));
                }
                catch (FileNotFoundException ex)
                {
                    Console.WriteLine(ex.Message);
                    Console.WriteLine("（量测子进程需要本机装有 Rhino 8 才能运行；主程序其余功能不受影响）");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("几何量测失败：" + ex.Message);
                }
                return;
            }

            var r = SegmentSolver.Solve(p);
            if (!r.Ok) { Console.WriteLine("FAIL: " + r.Message); return; }
            Console.WriteLine($"壁厚      {r.WallDesignMm:0.000} mm  (电学需 {r.WallElecMm:0.000})");
            Console.WriteLine($"损失      {r.LossPerMeterWPerM / 1000:0.00} kW/m   段功率 {r.PowerTotalW / 1000:0.00} kW");
            Console.WriteLine($"电流/压   {r.CurrentA:0} A / {r.VoltageV:0.00} V   J={r.JActualAPerMm2:0.00} A/mm²");
            Console.WriteLine($"铂重      管 {r.MassTubeKg:0.000} + 法兰 {r.MassFlangePairKg:0.000} = {r.MassTotalKg:0.000} kg");
            Console.WriteLine($"温度      中 {p.TSetC:0} / 最冷 {r.TMinC:0.0} / 法兰 {r.TFlangeAC:0.0} °C");
            Console.WriteLine($"析晶裕度  {r.DevitMarginMinK:+0.0;-0.0} K  {(r.DevitRisk ? "★风险" : "✓")}");
            Console.WriteLine($"法兰 Φ    {r.FlangePhi:0.000}  缺口 {r.FlangeDeficitW:0} W  浮温 {r.FlangeFloatTempC:0} °C");
            Console.WriteLine($"ℓt={r.DecayLengthMm:0.0}mm  τ={r.TauMetalS:0}s  稳定裕度 {r.StabilityRatio:0.0}×");
            Console.WriteLine($"法兰 剖面={p.FlangeShapeMode} 铂重={r.MassFlangePairKg * 1000:0}g " +
                              $"理想渐变={r.FlangeA.IdealMassKg * 2000:0}g r_o,max={r.FlangeA.ROMaxMm:0.0}mm");

            // --cli --figs <dir>   生成论文示意图
            if (args.Contains("--figs"))
            {
                int fi = Array.IndexOf(args, "--figs");
                string fdir = fi + 1 < args.Length && !args[fi + 1].StartsWith("--")
                              ? args[fi + 1] : "docs/fig";
                ApplicationConfiguration.Initialize();
                UI.Schematics.All(fdir);
                Console.WriteLine("示意图已输出到 " + fdir);
                return;
            }

            // --cli --glass   稳态验证：按真实控温点串联三段，算玻璃温降，与实测对照
            // 这是全模型唯一一个**拿现场实测校准**的点：入口 1150 → 出口 1130，全程降 20 K。
            // 玻璃温降由 hg、保温损失、产量三者共同决定，对不上说明底层热平衡有问题，
            // 那么升温核算与法兰温场优化都建立在错的底子上。
            if (args.Contains("--glass"))
            {
                var line = new (string Name, double TSet, double Head)[]
                { ("HC1", 1150, 0.3), ("HC2", 1080, 0.6), ("HC3", 1050, 1.0) };
                const double glassInC = 1150, glassOutMeasuredC = 1130;

                Console.WriteLine("=== 稳态验证：玻璃温降 vs 实测 ===");
                Console.WriteLine($"产量 {p.ThroughputTPerDay:0.0} t/day   内壁换热 hg {p.HGlass:0} W/m²K   " +
                                  $"壁厚 {p.WallMinMm:0.000} mm   段长 {p.TubeLengthMm:0} mm");
                Console.WriteLine();
                Console.WriteLine($"{"段",6}{"控温",8}{"玻璃进",9}{"玻璃出",9}{"本段降",8}" +
                                  $"{"电流",8}{"功率 W",9}{"管根",9}{"衔接温差",10}  10K 目标");

                double tg = glassInC;
                double totalPower = 0;
                bool allOk = true;
                var draws = new double[line.Length];      // 各段法兰抽热 W（二维模型给出）
                int si = -1;

                foreach (var (name, tset, head) in line)
                {
                    si++;
                    var q = SegmentSolver.Clone(p);
                    q.TSetC = tset; q.TGlassInC = tg; q.GlassHeadM = head;
                    q.SizeWall = false;
                    q.SizeFlangeThickness = false;        // 校核现状：法兰厚度就取 3dm 实测 2.0 mm

                    // ★ 必须走耦合解。裸调 SegmentSolver.Solve 会让 defTab 落到已作废的
                    //   FlangeRadial（一维环形），Φ 算成 0.037 而真值 0.72–0.82，
                    //   法兰抽热高估约 20 倍，冷点假深到 200 K 以上。见 HANDOVER §5 / §7。
                    SolveResult sr;
                    try
                    {
                        var c = CoupledSolver.Solve(q, new FlangePlate());
                        if (!c.Tube.Ok) { Console.WriteLine($"{name,6}  ✗ {c.Tube.Message}"); allOk = false; break; }
                        sr = c.Tube;
                        draws[si] = c.FlangeDrawW;
                    }
                    catch (Exception ex) { Console.WriteLine($"{name,6}  ✗ {ex.Message}"); allOk = false; break; }

                    double drop = tg - sr.TGlassOutC;
                    double dRoot = tset - sr.TFlangeAC;         // 管中点设定 − 法兰衔接处
                    totalPower += sr.PowerTotalW;
                    Console.WriteLine($"{name,6}{tset,8:0}{tg,9:0.0}{sr.TGlassOutC,9:0.0}{drop,8:+0.0;-0.0}" +
                                      $"{sr.CurrentA,8:0}{sr.PowerTotalW,9:0}{sr.TFlangeAC,9:0.0}" +
                                      $"{dRoot,10:+0.0;-0.0}  {(Math.Abs(dRoot) <= 10 ? "✓" : "✗ 超")}");
                    tg = sr.TGlassOutC;
                }

                if (allOk)
                {
                    double calc = glassInC - tg, meas = glassInC - glassOutMeasuredC;
                    Console.WriteLine();
                    Console.WriteLine($"全程玻璃温降   模型 {calc:0.0} K   实测 {meas:0.0} K   " +
                                      $"偏差 {calc - meas:+0.0;-0.0} K");
                    Console.WriteLine($"三段总电功率   {totalPower:0} W");
                    Console.WriteLine();
                    if (Math.Abs(calc - meas) <= 5)
                        Console.WriteLine("✓ 与实测一致 —— 底层热平衡（hg / 保温损失 / 产量）可信，");
                    else if (calc > meas)
                        Console.WriteLine("✗ 模型降得太多 —— 散热偏大或 hg 偏大：查保温层数据、发射率、产量。");
                    else
                        Console.WriteLine("✗ 模型降得太少 —— 散热偏小或 hg 偏小：查保温层数据、发射率、产量。");
                    Console.WriteLine("  「衔接温差」为控温点与法兰处管温之差，目标 ≤10 K。");

                    // ── 反解 hg：实测温降是硬数据，用它标定内壁换热系数，而不是继续猜。
                    //    hg 越大 → 玻璃向金属放热越多 → 出口越低，单调，可二分。
                    // 反解时复用上面二维模型给出的抽热 D（FlangeDrawOverrideW），
                    // 而不是每次重跑耦合解（40 次二分 × 3 段 × 1 min 不可接受）。
                    // 近似：D 主要由法兰自身热状态与管根温度决定，对 hg 只有二阶依赖。
                    double GlassOut(double hg)
                    {
                        double t = glassInC;
                        for (int k = 0; k < line.Length; k++)
                        {
                            var (_, tset, head) = line[k];
                            var q = SegmentSolver.Clone(p);
                            q.HGlass = hg; q.TSetC = tset; q.TGlassInC = t;
                            q.GlassHeadM = head; q.SizeWall = false;
                            // ← 二维模型的抽热，不走 FlangeRadial
                            q.FlangeDrawOverrideW = draws[k]; q.FlangeDrawOverrideSet = true;
                            var s = SegmentSolver.Solve(q);
                            if (!s.Ok) return double.NaN;
                            t = s.TGlassOutC;
                        }
                        return t;
                    }

                    Console.WriteLine();
                    Console.WriteLine("=== 反解内壁换热系数 hg ===");
                    double lo = 5, hi = p.HGlass;
                    double fLo = GlassOut(lo), fHi = GlassOut(hi);
                    if (double.IsNaN(fLo) || double.IsNaN(fHi) ||
                        (fLo - glassOutMeasuredC) * (fHi - glassOutMeasuredC) > 0)
                    {
                        Console.WriteLine($"  区间 [{lo:0},{hi:0}] 未包住实测值" +
                                          $"（出口 {fLo:0.0} … {fHi:0.0} °C，实测 {glassOutMeasuredC:0}）—— " +
                                          "说明偏差不只来自 hg，需同时查发射率与保温层数据。");
                    }
                    else
                    {
                        for (int k = 0; k < 40 && hi - lo > 1e-3; k++)
                        {
                            double mid = 0.5 * (lo + hi);
                            if ((GlassOut(mid) - glassOutMeasuredC) * (fLo - glassOutMeasuredC) > 0)
                            { lo = mid; fLo = GlassOut(mid); }
                            else hi = mid;
                        }
                        double hgFit = 0.5 * (lo + hi);
                        Console.WriteLine($"  能复现实测 20 K 温降的 hg = {hgFit:0.0} W/m²K" +
                                          $"（当前设定 {p.HGlass:0}，比值 {hgFit / p.HGlass:0.00}）");
                        Console.WriteLine($"  校核：层流 Nu≈3.66 下 k_eff = hg·D/Nu = " +
                                          $"{hgFit * p.TubeIdMm * 1e-3 / 3.66:0.00} W/m·K");
                        Console.WriteLine("  玻璃熔体导热约 1.0～1.5（含辐射贡献可更高）—— 据此判断该值是否合理。");
                    }
                }
                return;
            }

            // --cli --clampfit  用实测玻璃温降反标定铜排夹持温度
            //
            // 动机：BusbarClampTempC 默认 −1 = 无夹冷、自由辐射端。该假设让舌片烧到 1240 °C，
            // 而 ρ(1240 °C)≈48 比 ρ(200 °C)≈17 大 2.8 倍（用户实测电阻率表），
            // 舌片高温把自身发热又抬高近 3 倍 —— 于是「法兰发热 651 W ×2 ≈ 管段 1373 W」，
            // 功率几乎全耗在法兰上，只能靠那个假的辐射出口排掉。这是自洽但错误的分支。
            // HANDOVER §4.5 早写过「关键是有没有夹冷」，而默认值恰恰落在「没有」那一侧。
            if (args.Contains("--clampfit"))
            {
                var line = new (string Name, double TSet, double Head)[]
                { ("HC1", 1150, 0.3), ("HC2", 1080, 0.6), ("HC3", 1050, 1.0) };
                const double glassInC = 1150, glassOutMeasuredC = 1130;
                double measDrop = glassInC - glassOutMeasuredC;
                double[] clamps = { -1, 900, 700, 500, 300, 150, 80 };

                Console.WriteLine("=== 铜排夹持温度反标定（用实测 20 K 玻璃温降定边界条件）===");
                Console.WriteLine($"产量 {p.ThroughputTPerDay:0.0} t/day   hg {p.HGlass:0}   " +
                                  $"壁厚 {p.WallMinMm:0.000} mm   法兰 2.0 mm（3dm 实测）");
                Console.WriteLine("夹持「无」= BusbarClampTempC −1 = 自由辐射端 = 当前默认值");
                Console.WriteLine();
                Console.WriteLine($"{"夹持 °C",10}{"玻璃出",9}{"全程降",9}{"vs 实测",10}" +
                                  $"{"抽热 W/片",11}{"最深衔接温差",14}{"总功率 W",10}  判定");

                double bestErr = double.MaxValue, bestClamp = double.NaN;
                foreach (double tc in clamps)
                {
                    double tg = glassInC, maxD = 0, totP = 0, drawSum = 0;
                    bool ok = true;
                    foreach (var (_, tset, head) in line)
                    {
                        var q = SegmentSolver.Clone(p);
                        q.TSetC = tset; q.TGlassInC = tg; q.GlassHeadM = head;
                        q.SizeWall = false; q.SizeFlangeThickness = false;
                        q.BusbarClampTempC = tc;
                        try
                        {
                            var c = CoupledSolver.Solve(q, new FlangePlate());
                            if (!c.Tube.Ok) { ok = false; break; }
                            tg = c.Tube.TGlassOutC;
                            maxD = Math.Max(maxD, Math.Abs(tset - c.Tube.TFlangeAC));
                            totP += c.Tube.PowerTotalW;
                            drawSum += c.FlangeDrawW;
                        }
                        catch { ok = false; break; }
                    }
                    string label = tc < 0 ? "无" : tc.ToString("0");
                    if (!ok) { Console.WriteLine($"{label,10}   ✗ 求解失败"); continue; }

                    double drop = glassInC - tg, err = drop - measDrop;
                    if (Math.Abs(err) < bestErr) { bestErr = Math.Abs(err); bestClamp = tc; }
                    Console.WriteLine($"{label,10}{tg,9:0.0}{drop,9:0.0}{err,10:+0.0;-0.0}" +
                        $"{drawSum / line.Length,11:0}{maxD,14:0.0}{totP,10:0}  " +
                        $"{(Math.Abs(err) <= 3 ? "✓ 接近实测" : "")}{(maxD <= 10 ? " ✓ 达 10K 目标" : "")}");
                }

                Console.WriteLine();
                if (!double.IsNaN(bestClamp))
                    Console.WriteLine($"最接近实测的夹持温度：{(bestClamp < 0 ? "无夹冷" : bestClamp.ToString("0") + " °C")}" +
                                      $"（温降偏差 {bestErr:0.0} K）");
                Console.WriteLine("若「无夹冷」明显偏离而有夹冷的各档都接近，则默认边界条件是错的 ——");
                Console.WriteLine("这一条会同时改变冷点深度、玻璃温降与 hg 的标定值，前面所有数值结论都要重跑。");
                return;
            }

            // --cli --flangefit  规程二：扫法兰厚度，找能把衔接温差压到 10 K 的自给点
            //
            // 依据：冷点深度 |ΔT_dip| = D/√(k·A·β)，D 是法兰从管子抽走的热。
            // 法兰自身焦耳热 ∝ J²∝1/t²，而散热与厚度基本无关 ⇒ **减薄反而让法兰更热、更自给**。
            // 自给率 Φ→1 时 D→0，冷点随之消失。这可能是「省铂」与「≤10 K」的共同解。
            if (args.Contains("--flangefit"))
            {
                // 取最不利段：金属最冷、与玻璃温差最大
                double tset = 1050, tglass = 1130, head = 1.0, clamp = 80;
                Console.WriteLine("=== 规程二：法兰厚度 → 衔接温差 ===");
                Console.WriteLine($"取最不利段 HC3：控温 {tset:0} °C，玻璃 {tglass:0} °C，" +
                                  $"水头 {head:0.0} m，铜排夹持 {clamp:0} °C");
                Console.WriteLine("目标：控温点与法兰处管温之差 ≤ 10 K");
                Console.WriteLine();
                Console.WriteLine($"{"法兰厚 mm",11}{"抽热 W/片",11}{"自给率 Φ",11}{"衔接温差 K",12}" +
                                  $"{"法兰 J",10}{"舌端 °C",10}{"两片铂重 g",12}  判定");

                foreach (double ft in new[] { 3.0, 2.0, 1.5, 1.0, 0.8, 0.6, 0.5, 0.4 })
                {
                    var q = SegmentSolver.Clone(p);
                    q.TSetC = tset; q.TGlassInC = tglass; q.GlassHeadM = head;
                    q.SizeWall = false; q.SizeFlangeThickness = false;
                    q.BusbarClampTempC = clamp;
                    var g = new FlangePlate { ThicknessMm = ft, ThickenedMm = ft };
                    try
                    {
                        var c = CoupledSolver.Solve(q, g);
                        if (!c.Tube.Ok) { Console.WriteLine($"{ft,11:0.00}   ✗ {c.Tube.Message}"); continue; }
                        // 有符号：>0 = 法兰比管冷（安全），≤0 = 法兰比管热（倒灌，§4.2k 硬安全线）
                        double dRoot = tset - c.Tube.TFlangeAC;
                        string v = (RootDeltaOk(dRoot) ? "✓ 达 10K" : dRoot <= 0 ? "★ 倒灌 Φ>1" : "")
                                 + (c.JFlangeMaxAPerMm2 > p.JAllowAPerMm2 ? "  · J 超参考值" : "");
                        Console.WriteLine($"{ft,11:0.00}{c.FlangeDrawW,11:0.0}{c.Flange.PhiOverall,11:0.000}" +
                            $"{dRoot,12:+0.0;-0.0}{c.JFlangeMaxAPerMm2,10:0.00}{c.Flange.TTabEndMeanC,10:0.0}" +
                            $"{c.MassFlangePairG,12:0}  {v}");
                        SaveFields(q, c, $"flange_t{ft:0.00}");
                    }
                    catch (Exception ex) { Console.WriteLine($"{ft,11:0.00}   ✗ {ex.Message}"); }
                }
                Console.WriteLine();
                Console.WriteLine("Φ→1 表示法兰自身焦耳热足以覆盖自身散热，不再从管子抽热 ⇒ 冷点消失。");
                Console.WriteLine("减薄使 J↑、发热 ∝J² 而散热基本不变 —— 故省铂与压冷点可能同向，需看 J 是否越界。");
                return;
            }

            // --cli --fit2d  管壁 × 法兰厚 二维：对每个管壁二分法兰厚，找衔接温差过零点
            //
            // 单扫法兰厚已证明在现状管壁下无解（Φ=1 处 J≈12.9 超限 29%）。标度关系：
            //   I ∝ √t_管 ,  t*_法兰|_{Φ=1} ∝ I² ,  J|_{t*} = I/t* ∝ 1/I
            // ⇒ **电流越低，Φ=1 点的 J 反而越高**。故减薄管壁会让法兰更难做，
            //   要把 J 压回许用值反而要加厚管壁。本扫描验证这个反直觉的结论。
            if (args.Contains("--fit2d"))
            {
                double tset = 1050, tglass = 1130, head = 1.0, clamp = 80;
                Console.WriteLine("=== 管壁 × 法兰厚：衔接温差过零轨迹 ===");
                Console.WriteLine($"最不利段 HC3：控温 {tset:0} °C，玻璃 {tglass:0} °C，铜排夹持 {clamp:0} °C");
                Console.WriteLine("对每个管壁二分法兰厚度，求「控温点 − 法兰处管温」= 0 的厚度");
                Console.WriteLine();

                // 有符号温差：>0 = 冷点（法兰抽热），<0 = 热包（法兰倒灌）
                (double d, double jf, double jt, double m, bool ok) Probe(double wall, double ft)
                {
                    var q = SegmentSolver.Clone(p);
                    q.TSetC = tset; q.TGlassInC = tglass; q.GlassHeadM = head;
                    q.WallMinMm = wall; q.SizeWall = false;
                    q.SizeFlangeThickness = false; q.BusbarClampTempC = clamp;
                    try
                    {
                        var c = CoupledSolver.Solve(q, new FlangePlate { ThicknessMm = ft, ThickenedMm = ft });
                        if (!c.Tube.Ok) return (0, 0, 0, 0, false);
                        return (tset - c.Tube.TFlangeAC, c.JFlangeMaxAPerMm2, c.JTubeAPerMm2,
                                c.MassTubeG + c.MassFlangePairG, true);
                    }
                    catch { return (0, 0, 0, 0, false); }
                }

                Console.WriteLine($"{"管壁 mm",9}{"法兰厚 mm",11}{"温差 K",9}{"管 J",8}{"法兰 J",9}" +
                                  $"{"单段总铂 g",12}  判定");

                foreach (double wall in new[] { 0.60, 0.80, 1.00, 1.30, 1.60, 2.00 })
                {
                    // 温差随法兰增厚单调上升（薄→倒灌为负，厚→抽热为正），可二分。
                    // ★ 靶不是 0 而是 RootDeltaTargetK：二分到 0 就是把设计点放在 Φ=1 的悬崖边（§4.2k）
                    double Dev(double d) => d - RootDeltaTargetK;
                    double lo = 0.6, hi = 3.5;
                    var fLo = Probe(wall, lo);
                    var fHi = Probe(wall, hi);
                    if (!fLo.ok || !fHi.ok || Dev(fLo.d) * Dev(fHi.d) > 0)
                    {
                        Console.WriteLine($"{wall,9:0.00}   ✗ 区间 [{lo:0.0},{hi:0.0}] 未包住靶值 " +
                                          $"{RootDeltaTargetK:0.#} K（温差 {fLo.d:+0.0;-0.0} … {fHi.d:+0.0;-0.0} K）");
                        continue;
                    }
                    for (int k = 0; k < 9; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        var f = Probe(wall, mid);
                        if (!f.ok) break;
                        if (Dev(f.d) * Dev(fLo.d) > 0) { lo = mid; fLo = f; } else hi = mid;
                    }
                    double t2 = 0.5 * (lo + hi);
                    var r2 = Probe(wall, t2);
                    string v = (RootDeltaOk(r2.d) ? "✓ 温差达标" : r2.d <= 0 ? "★ 倒灌 Φ>1" : "✗ 温差")
                             + (r2.jf <= p.JAllowAPerMm2 && r2.jt <= p.JAllowAPerMm2 ? "  ✓ J" : "  · J 超参考值");
                    Console.WriteLine($"{wall,9:0.00}{t2,11:0.000}{r2.d,9:+0.0;-0.0}{r2.jt,8:0.00}" +
                                      $"{r2.jf,9:0.00}{r2.m,12:0}  {v}");
                }
                Console.WriteLine();
                Console.WriteLine("若「J 达标」只在大管壁处出现，则 ≤10K 目标要花铂金买，不是省铂的顺风车。");
                Console.WriteLine("温差对法兰厚的斜率约 262 K/mm ⇒ 守住 ±10K 需法兰厚公差约 ±0.04 mm，");
                Console.WriteLine("这是实打实的制造要求，须写进交付条件。");
                return;
            }

            // --cli --discfit  法兰圆盘直径 × 法兰厚：第三个自由度
            //
            // 直径同时动三件事，方向相反：
            //   大 ⇒ 散热面积↑ ⇒ 自身散热↑ ⇒ Φ↓（温差变差）、铂重↑
            //   大 ⇒ 孔周外导电截面↑ ⇒ 法兰 J↓（J 变好）
            // 缩小到孔周环宽不足时 J 会爆掉 ⇒ 应存在最优。
            // 与「厚度」那个冲突相反，缩小直径可能是省铂与压温差**同向**的方向。
            if (args.Contains("--discfit"))
            {
                double tset = 1050, tglass = 1130, head = 1.0, clamp = 80, wall = 1.00;
                Console.WriteLine("=== 法兰圆盘直径 → 温差过零点处的 J 与铂重 ===");
                Console.WriteLine($"最不利段 HC3：控温 {tset:0} °C，玻璃 {tglass:0} °C，" +
                                  $"铜排夹持 {clamp:0} °C，管壁固定 {wall:0.00} mm");
                Console.WriteLine($"管孔半径固定 26.0 mm（= 管外半径）。对每个圆盘半径二分法兰厚，求温差 = 0");
                Console.WriteLine();

                (double d, double jf, double phi, double m, double gen, bool ok) Probe(double ro, double ft)
                {
                    var q = SegmentSolver.Clone(p);
                    q.TSetC = tset; q.TGlassInC = tglass; q.GlassHeadM = head;
                    q.WallMinMm = wall; q.SizeWall = false;
                    q.SizeFlangeThickness = false; q.BusbarClampTempC = clamp;
                    var g = new FlangePlate { DiscRadiusMm = ro, ThicknessMm = ft, ThickenedMm = ft };
                    try
                    {
                        var c = CoupledSolver.Solve(q, g);
                        if (!c.Tube.Ok) return (0, 0, 0, 0, 0, false);
                        return (tset - c.Tube.TFlangeAC, c.JFlangeMaxAPerMm2, c.Flange.PhiOverall,
                                c.MassTubeG + c.MassFlangePairG, c.Flange.QGenW, true);
                    }
                    catch { return (0, 0, 0, 0, 0, false); }
                }

                Console.WriteLine($"{"圆盘半径 mm",12}{"孔周环宽",10}{"法兰厚 mm",11}{"温差 K",9}" +
                                  $"{"法兰 J",9}{"Φ",8}{"自身发热 W",12}{"单段总铂 g",12}  判定");

                // 2026-08-10 保温改为实况（纤维 2.5 mm、仅圆盘保温）后散热大增，
                // Φ=1 点右移到极薄法兰、J≈24（许用 10 的 2.4 倍）⇒ 现状 Ø120 下无解。
                // 缩小直径同时降散热与 J（机理见 §4.2d），故向小半径重扫。
                // 下限受孔半径 26 限制：R=32 时孔周环宽仅 6 mm，网格 h=1.0 只有 6 格，偏粗。
                foreach (double ro in new[] { 32.0, 35.0, 38.0, 42.0, 46.0, 50.0 })
                {
                    // 上界要够大：圆盘越小散热越少，回到 Φ=1 所需的法兰**越厚**。
                    // R=32 在 3.5 mm 上界处温差仍为 −11.5 K（仍在倒灌），故放宽到 8.0。
                    // ★ 靶为 RootDeltaTargetK 而非 0 —— 见该常数的说明（§4.2k）
                    double Dev(double d) => d - RootDeltaTargetK;
                    double lo = 0.4, hi = 8.0;
                    var fLo = Probe(ro, lo);
                    var fHi = Probe(ro, hi);
                    if (!fLo.ok || !fHi.ok)
                    {
                        Console.WriteLine($"{ro,12:0.0}   ✗ 端点求解失败" +
                                          $"（薄端 {(fLo.ok ? "ok" : "fail")}，厚端 {(fHi.ok ? "ok" : "fail")}）");
                        continue;
                    }
                    if (Dev(fLo.d) * Dev(fHi.d) > 0)
                    {
                        Console.WriteLine($"{ro,12:0.0}   ✗ 区间 [{lo:0.0},{hi:0.0}] 内未跨过靶值 " +
                                          $"{RootDeltaTargetK:0.#} K（{fLo.d:+0.0;-0.0} … {fHi.d:+0.0;-0.0} K）");
                        continue;
                    }
                    for (int k = 0; k < 8; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        var f = Probe(ro, mid);
                        if (!f.ok) break;
                        if (Dev(f.d) * Dev(fLo.d) > 0) { lo = mid; fLo = f; } else hi = mid;
                    }
                    double t2 = 0.5 * (lo + hi);
                    var r2 = Probe(ro, t2);
                    string v = (RootDeltaOk(r2.d) ? "✓ 温差" : r2.d <= 0 ? "★ 倒灌 Φ>1" : "✗ 温差")
                             + (r2.jf <= p.JAllowAPerMm2 ? "  ✓ J" : "  · J 超参考值");
                    Console.WriteLine($"{ro,12:0.0}{ro - 26.0,10:0.0}{t2,11:0.000}{r2.d,9:+0.0;-0.0}" +
                                      $"{r2.jf,9:0.00}{r2.phi,8:0.000}{r2.gen,12:0}{r2.m,12:0}  {v}");

                    // 每个算例都出二维场图（温度 + 电流密度 + 管轴向剖面）
                    var qs = SegmentSolver.Clone(p);
                    qs.TSetC = tset; qs.TGlassInC = tglass; qs.GlassHeadM = head;
                    qs.WallMinMm = wall; qs.SizeWall = false;
                    qs.SizeFlangeThickness = false; qs.BusbarClampTempC = clamp;
                    try
                    {
                        var cs = CoupledSolver.Solve(qs,
                            new FlangePlate { DiscRadiusMm = ro, ThicknessMm = t2, ThickenedMm = t2 });
                        if (cs.Tube.Ok) SaveFields(qs, cs, $"disc_R{ro:0}_t{t2:0.00}");
                    }
                    catch { Console.WriteLine("    ⚠ 场图算例求解失败"); }
                }
                Console.WriteLine();
                Console.WriteLine("若 J 随直径缩小而恶化、铂重却同步下降，则存在最优直径 ——");
                Console.WriteLine("这与「厚度」那个冲突方向相反，是省铂与压温差可能同向的自由度。");
                return;
            }

            // --cli --wallfit [圆盘半径]   在已优化的法兰直径下，管壁还能不能再降
            //
            // --discfit 把管壁钉在 1.00 mm，得到 R=42~44 时法兰 J 只有 8.5~9.0，
            // 距许用值 10 还有余量。本扫描把这点余量拿去换管壁。
            // 但方向不利：减薄管壁 ⇒ 电流↓ ⇒ J|_{Φ=1} ∝ 1/√t_管 ⇒ **余量被吃掉**。
            // 同时升温下界（--ramp）是硬底。故必存在下限，扫出来。
            if (args.Contains("--wallfit"))
            {
                int wi = Array.IndexOf(args, "--wallfit");
                double ro = wi + 1 < args.Length && double.TryParse(args[wi + 1], out var rv) ? rv : 44.0;
                double tset = 1050, tglass = 1130, head = 1.0, clamp = 80;
                const double rampTargetC = 1150, rampFromC = 25, rampHours = 3.0;

                Console.WriteLine("=== 管壁下探（法兰直径已优化）===");
                Console.WriteLine($"最不利段 HC3：控温 {tset:0} °C，玻璃 {tglass:0} °C，铜排夹持 {clamp:0} °C");
                Console.WriteLine($"圆盘半径固定 {ro:0.0} mm（Ø{2 * ro:0}）；对每个管壁二分法兰厚求温差 = 0");
                Console.WriteLine($"升温同时校核：空管 {rampFromC:0}→{rampTargetC:0} °C / {rampHours:0.#} h");
                Console.WriteLine();

                (double d, double jf, double jt, double mTube, double mFl, double gen, double iA, bool ok)
                Probe(double wall, double ft)
                {
                    var q = SegmentSolver.Clone(p);
                    q.TSetC = tset; q.TGlassInC = tglass; q.GlassHeadM = head;
                    q.WallMinMm = wall; q.SizeWall = false;
                    q.SizeFlangeThickness = false; q.BusbarClampTempC = clamp;
                    var g = new FlangePlate { DiscRadiusMm = ro, ThicknessMm = ft, ThickenedMm = ft };
                    try
                    {
                        var c = CoupledSolver.Solve(q, g);
                        if (!c.Tube.Ok) return (0, 0, 0, 0, 0, 0, 0, false);
                        return (tset - c.Tube.TFlangeAC, c.JFlangeMaxAPerMm2, c.JTubeAPerMm2,
                                c.MassTubeG, c.MassFlangePairG, c.Flange.QGenW, c.Tube.CurrentA, true);
                    }
                    catch { return (0, 0, 0, 0, 0, 0, 0, false); }
                }

                Console.WriteLine($"{"管壁 mm",9}{"法兰厚 mm",11}{"温差 K",9}{"管 J",8}{"法兰 J",9}" +
                                  $"{"管 g",8}{"法兰 g",9}{"单段总铂 g",12}{"升温",8}  判定");

                foreach (double wall in new[] { 1.00, 0.90, 0.80, 0.70, 0.60, 0.55 })
                {
                    // ★ 靶为 RootDeltaTargetK 而非 0（§4.2k）
                    double Dev(double d) => d - RootDeltaTargetK;
                    double lo = 0.4, hi = 4.0;
                    var fLo = Probe(wall, lo);
                    var fHi = Probe(wall, hi);
                    if (!fLo.ok || !fHi.ok)
                    { Console.WriteLine($"{wall,9:0.00}   ✗ 端点求解失败"); continue; }
                    if (Dev(fLo.d) * Dev(fHi.d) > 0)
                    { Console.WriteLine($"{wall,9:0.00}   ✗ [{lo:0.0},{hi:0.0}] 内未跨过靶值 {RootDeltaTargetK:0.#} K"); continue; }

                    for (int k = 0; k < 8; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        var f = Probe(wall, mid);
                        if (!f.ok) break;
                        if (Dev(f.d) * Dev(fLo.d) > 0) { lo = mid; fLo = f; } else hi = mid;
                    }
                    double t2 = 0.5 * (lo + hi);
                    var r2 = Probe(wall, t2);

                    // 升温用该方案的实际法兰质量、自身发热与对应电流
                    // （RampSolver 据此反推法兰电阻，升温时法兰既发热也散热）
                    var rp = RampSolver.Solve(p, wall, r2.mFl, r2.gen, r2.iA, tset,
                                              rampFromC, rampTargetC, rampHours);
                    string v = (RootDeltaOk(r2.d) ? "✓温差" : r2.d <= 0 ? "★倒灌Φ>1" : "✗温差")
                             + (r2.jf <= p.JAllowAPerMm2 && r2.jt <= p.JAllowAPerMm2 ? " ✓J" : " ·J超参考")
                             + (rp.Reached ? "" : " ✗升不到");
                    Console.WriteLine($"{wall,9:0.00}{t2,11:0.000}{r2.d,9:+0.0;-0.0}{r2.jt,8:0.00}" +
                        $"{r2.jf,9:0.00}{r2.mTube,8:0}{r2.mFl,9:0}{r2.mTube + r2.mFl,12:0}" +
                        $"{(rp.Reached ? rp.HoursToTarget.ToString("0.00") + "h" : "✗"),8}  {v}");
                }
                Console.WriteLine();
                Console.WriteLine("管与法兰的质量都 ∝ 管壁（法兰厚 t* ∝ I² ∝ 管壁），故总铂近似线性下降；");
                Console.WriteLine("下限由「法兰 J 顶到许用值」或「升温升不到」两者中先到的那个决定。");
                return;
            }

            // --cli --lineopt [圆盘半径]   整线口径寻优：3 段 × 4 片，逐片定尺
            //
            // 补 §4.2g 指出的缺口：此前所有扫描都是「单段 HC3 + 两片相同法兰」，不是整线设计。
            //
            // 【近似及其依据】严格解需三段联立（每片法兰被相邻两段共用、承受不同电流），
            // 而 CoupledSolver 解的是「一段 + 两端相同法兰」。此处用两步近似：
            //   ① 对每段单独二分法兰厚，求该段衔接温差 = 0 的厚度 t_seg（Φ≈1）
            //   ② 每片按其实际承担电流折算：t ∝ I（定尺后 J=J_allow，而 J=K/t、K ∝ I）
            //      共用片取相邻两段折算值的**较大者**（保守）
            // 共用片电流用 LineSolver.JointCurrentA，即工作簿的 (I_左+I_右)/2 × 1.5。
            // ⚠ 那个 1.5 是经验系数（§6 待补数据 ⑧），本命令的结论精度受限于它。
            if (args.Contains("--lineopt"))
            {
                int li = Array.IndexOf(args, "--lineopt");
                double ro = li + 1 < args.Length && double.TryParse(args[li + 1], out var rv) ? rv : 44.0;
                double clamp = 80;
                var segs = new List<Segment>
                {
                    new(){ Name="HC1", TSetC=1150, TGlassInC=1150, GlassHeadM=0.3, LengthMm=300, WallMm=1.0 },
                    new(){ Name="HC2", TSetC=1080, TGlassInC=1143, GlassHeadM=0.6, LengthMm=300, WallMm=1.0 },
                    new(){ Name="HC3", TSetC=1050, TGlassInC=1137, GlassHeadM=1.0, LengthMm=300, WallMm=1.0 },
                };

                Console.WriteLine($"=== 整线寻优（{segs.Count} 段 → {LineSolver.FlangeCount(segs.Count)} 片）===");
                Console.WriteLine($"圆盘半径 {ro:0.0} mm（Ø{2 * ro:0}）  铜排夹持 {clamp:0} °C");
                Console.WriteLine($"每段二分法兰厚求温差 = {RootDeltaTargetK:0.#} K（安全侧，§4.2k），" +
                                  "再按各片实际电流折算 t ∝ I");
                Console.WriteLine();

                var tNeed = new double[segs.Count];
                var iSeg = new double[segs.Count];
                var jRef = new double[segs.Count];

                Console.WriteLine($"{"段",6}{"控温",7}{"电流 A",9}{"靶温差处的法兰厚",16}{"该厚度下 J",12}  判定");
                for (int i = 0; i < segs.Count; i++)
                {
                    var s = segs[i];
                    (double d, double jf, double iA, bool ok) Probe(double ft)
                    {
                        var q = SegmentSolver.Clone(p);
                        q.TSetC = s.TSetC; q.TGlassInC = s.TGlassInC; q.GlassHeadM = s.GlassHeadM;
                        q.WallMinMm = s.WallMm; q.SizeWall = false;
                        q.SizeFlangeThickness = false; q.BusbarClampTempC = clamp;
                        try
                        {
                            var c = CoupledSolver.Solve(q,
                                new FlangePlate { DiscRadiusMm = ro, ThicknessMm = ft, ThickenedMm = ft });
                            return c.Tube.Ok
                                ? (s.TSetC - c.Tube.TFlangeAC, c.JFlangeMaxAPerMm2, c.Tube.CurrentA, true)
                                : (0, 0, 0, false);
                        }
                        catch { return (0, 0, 0, false); }
                    }

                    // ★ 靶为 RootDeltaTargetK 而非 0（§4.2k）
                    double Dev(double d) => d - RootDeltaTargetK;
                    double lo = 0.4, hi = 8.0;
                    var fLo = Probe(lo); var fHi = Probe(hi);
                    if (!fLo.ok || !fHi.ok || Dev(fLo.d) * Dev(fHi.d) > 0)
                    { Console.WriteLine($"{s.Name,6}   ✗ 未跨过靶值或求解失败"); tNeed[i] = double.NaN; continue; }
                    for (int k = 0; k < 8; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        var f = Probe(mid);
                        if (!f.ok) break;
                        if (Dev(f.d) * Dev(fLo.d) > 0) { lo = mid; fLo = f; } else hi = mid;
                    }
                    double t2 = 0.5 * (lo + hi);
                    var r2 = Probe(t2);
                    tNeed[i] = t2; iSeg[i] = r2.iA; jRef[i] = r2.jf;
                    Console.WriteLine($"{s.Name,6}{s.TSetC,7:0}{r2.iA,9:0}{t2,16:0.000}{r2.jf,12:0.00}" +
                                      $"  {(r2.jf <= p.JAllowAPerMm2 ? "✓ J" : "✗ J 越界")}");
                }
                if (tNeed.Any(double.IsNaN)) { Console.WriteLine("\n✗ 有段无解，终止"); return; }

                // ── 逐片折算
                Console.WriteLine();
                Console.WriteLine($"{"接头",12}{"共用",6}{"电流 A",9}{"厚度 mm",10}{"J",8}{"铂重 g",10}  依据");
                var proto = new FlangePlate { DiscRadiusMm = ro };
                double plateArea = CoupledSolver.PlateArea(proto);
                double flangeTotal = 0;
                int nF = LineSolver.FlangeCount(segs.Count);
                for (int k = 0; k < nF; k++)
                {
                    int left = k - 1, right = k;               // 第 k 片位于 segs[k-1] 与 segs[k] 之间
                    bool shared = left >= 0 && right < segs.Count;
                    double iJoint = LineSolver.JointCurrentA(iSeg, k);
                    // t ∝ I：取相邻两段各自折算值的较大者（保守）。
                    // ★ 必须同时记住**是哪一段决定的**：J 的参考点要取那一段，
                    //   取错段会让 J 偏（J ∝ I/t，两个量都要用同一段的参考值）。
                    double t = 0; int bind = -1;
                    if (left >= 0)
                    {
                        double tl = tNeed[left] * iJoint / iSeg[left];
                        if (tl > t) { t = tl; bind = left; }
                    }
                    if (right < segs.Count)
                    {
                        double tr = tNeed[right] * iJoint / iSeg[right];
                        if (tr > t) { t = tr; bind = right; }
                    }
                    string basis = bind >= 0 ? segs[bind].Name : "—";
                    // J_joint = J_ref × (I_joint/I_ref) × (t_ref/t)
                    double j = bind >= 0
                        ? jRef[bind] * (iJoint / iSeg[bind]) * (tNeed[bind] / t) : 0;
                    double m = plateArea * t * Materials.PtDensity * 1e-6;
                    flangeTotal += m;
                    string name = left < 0 ? "入口" : right >= segs.Count ? "出口"
                                : $"{segs[left].Name}|{segs[right].Name}";
                    Console.WriteLine($"{name,12}{(shared ? "是" : "—"),6}{iJoint,9:0}{t,10:0.000}" +
                                      $"{j,8:0.00}{m,10:0}  {basis}");
                }

                double tubeTotal = segs.Sum(s =>
                    Math.PI * (Math.Pow(s.TubeIdMm * 0.5 + s.WallMm, 2) - Math.Pow(s.TubeIdMm * 0.5, 2))
                    * s.LengthMm * Materials.PtDensity * 1e-6);
                Console.WriteLine();
                Console.WriteLine($"整线：管 {tubeTotal:0} g + 法兰 {flangeTotal:0} g = {tubeTotal + flangeTotal:0} g" +
                                  $"   （现状实测 7141 g，--geom 校核）");
                Console.WriteLine($"省铂 {(7141 - tubeTotal - flangeTotal) / 7141 * 100:+0.0;-0.0} %");
                Console.WriteLine(LineSolver.UseWorkbookSharedFactor
                    ? "⚠ 共用片电流用工作簿经验系数 1.5（对照模式）"
                    : "共用片电流 = √(I₁²+I₂²+I₁I₂)（120° 相位差矢量差，见 §4.2h 推导）");
                return;
            }

            // --cli --mesh [dir]   把计算网格画出来（3 段管 + 4 片法兰）
            //
            // 模型是**混合维度**离散，一张图说不清，故分两张：
            //   ① 整线布置：管为一维轴向（每段 Nodes 个节点），4 片法兰为 4 个二维平面
            //   ② 法兰平面：二维掩膜网格，步长 h，掩膜外不参与求解
            if (args.Contains("--mesh"))
            {
                int mi = Array.IndexOf(args, "--mesh");
                string dir = mi + 1 < args.Length && !args[mi + 1].StartsWith("--") ? args[mi + 1] : "figs";
                Directory.CreateDirectory(dir);
                ApplicationConfiguration.Initialize();

                var g = new FlangePlate();
                double h = 1.0;
                int nSeg = 3, nFlange = LineSolver.FlangeCount(nSeg);
                double segLen = p.TubeLengthMm;

                Console.WriteLine("=== 计算网格 ===");
                Console.WriteLine($"管：一维轴向，{nSeg} 段 × {p.Nodes} 节点，段长 {segLen:0} mm" +
                                  $"（轴向步长 {segLen / (p.Nodes - 1):0.000} mm）");
                Console.WriteLine($"    径向不离散 —— 铂壁 {p.WallMinMm:0.0} mm、k≈70 W/m·K，径向温差可忽略");
                Console.WriteLine($"法兰：二维掩膜网格，{nFlange} 片，步长 h = {h:0.0} mm");

                // ── ⓪a 变步长壳网格（生成器①）：孔周加密、远场放粗
                {
                    var sm = FlangeMesher.Build(g, 0, hFine: 2.0, hCoarse: 11.0, fineRadius: 45.0);
                    var (fi, fb) = sm.FaceCounts();
                    double truth = CoupledSolver.PlateArea(g);      // --geom 校核过的真值
                    double err = (sm.TotalArea - truth) / truth * 100;

                    Console.WriteLine();
                    Console.WriteLine("=== 变步长壳网格（生成器①）===");
                    Console.WriteLine($"  单元 {sm.CellCount}（原均匀 1 mm 掩膜网格 23636，减少 " +
                                      $"{(1 - sm.CellCount / 23636.0) * 100:0}%）");
                    Console.WriteLine($"  面 内部 {fi} / 边界 {fb}");
                    Console.WriteLine($"  平面净面积 {sm.TotalArea:0.000} mm²   真值 {truth:0.000}   " +
                                      $"偏差 {err:+0.000;-0.000} %  {(Math.Abs(err) < 0.5 ? "✓" : "✗")}");
                    Console.WriteLine($"  体积 {sm.VolumeMm3:0.0} mm³ → 单片 " +
                                      $"{sm.VolumeMm3 * Materials.PtDensity * 1e-6:0.0} g");

                    var fp = UI.FieldPlots.NewPlot();
                    var plot = fp.Plot;
                    foreach (var cell in sm.Cells)
                    {
                        var xs2 = new double[5]; var zs2 = new double[5];
                        for (int k = 0; k <= 4; k++)
                        { var nd = sm.Nodes[cell[k % 4]]; xs2[k] = nd.X; zs2[k] = nd.Z; }
                        var s = plot.Add.Scatter(xs2, zs2);
                        s.Color = ScottPlot.Colors.SteelBlue.WithAlpha(0.55);
                        s.LineWidth = 0.5f; s.MarkerSize = 0;
                    }
                    int nc = 181; var cx2 = new double[nc]; var cz2 = new double[nc];
                    for (int k = 0; k < nc; k++)
                    {
                        double a = 2 * Math.PI * k / (nc - 1);
                        cx2[k] = g.HoleRadiusMm * Math.Cos(a); cz2[k] = g.HoleRadiusMm * Math.Sin(a);
                    }
                    var hh = plot.Add.Scatter(cx2, cz2);
                    hh.Color = ScottPlot.Colors.Red; hh.LineWidth = 2f; hh.MarkerSize = 0;
                    hh.LegendText = "管孔（细化区中心）";

                    plot.Title($"变步长壳网格：{sm.CellCount} 单元（孔周 2 mm / 远场 11 mm）" +
                               $"　净面积偏差 {err:+0.00;-0.00}%");
                    plot.XLabel("x [mm]"); plot.YLabel("z [mm]");
                    plot.ShowLegend();
                    plot.Axes.SetLimits(g.TabTipXMm - 5, g.DiscRadiusMm + 5,
                                        -g.DiscRadiusMm - 5, g.DiscRadiusMm + 5);
                    fp.Plot.SavePng(Path.Combine(dir, "mesh_graded.png"), 1400, 760);
                    Console.WriteLine($"  → {dir}/mesh_graded.png");
                }

                // ── ⓪ 三维壳网格（管为圆柱壳，法兰为平板，装配在一起）
                //    ScottPlot 是纯 2D 库，故自做斜投影：Y(管轴)→水平，Z→竖直，X→斜向后
                //      u = Y + 0.433·X ,  v = Z + 0.25·X
                //    这套网格生成代码就是将来三维壳求解器的网格生成部分，不是一次性画图。
                {
                    static (double u, double v) Proj(double X, double Y, double Z)
                        => (Y + 0.433 * X, Z + 0.25 * X);

                    var fp = UI.FieldPlots.NewPlot();
                    var plot = fp.Plot;
                    double rOut = p.TubeIdMm * 0.5 + p.WallMinMm;
                    int nTheta = 36, nAxial = 13;           // 每段轴向 13 条环线（画图用，非求解密度）

                    // 管：圆柱壳。环向线 + 轴向线
                    for (int k = 0; k < nSeg; k++)
                        for (int a = 0; a < nAxial; a++)
                        {
                            double y = k * segLen + a * segLen / (nAxial - 1);
                            var us = new double[nTheta + 1]; var vs = new double[nTheta + 1];
                            for (int t = 0; t <= nTheta; t++)
                            {
                                double th = 2 * Math.PI * t / nTheta;
                                var q = Proj(rOut * Math.Cos(th), y, rOut * Math.Sin(th));
                                us[t] = q.u; vs[t] = q.v;
                            }
                            var s = plot.Add.Scatter(us, vs);
                            s.Color = ScottPlot.Colors.SteelBlue.WithAlpha(0.55);
                            s.LineWidth = 0.8f; s.MarkerSize = 0;
                            if (k == 0 && a == 0) s.LegendText = "铂金管（圆柱壳）";
                        }
                    for (int t = 0; t < nTheta; t += 3)
                    {
                        double th = 2 * Math.PI * t / nTheta;
                        var q0 = Proj(rOut * Math.Cos(th), 0, rOut * Math.Sin(th));
                        var q1 = Proj(rOut * Math.Cos(th), nSeg * segLen, rOut * Math.Sin(th));
                        var s = plot.Add.Scatter(new[] { q0.u, q1.u }, new[] { q0.v, q1.v });
                        s.Color = ScottPlot.Colors.SteelBlue.WithAlpha(0.45);
                        s.LineWidth = 0.8f; s.MarkerSize = 0;
                    }

                    // 法兰：每片一块平板，画外轮廓 + 内部网格线
                    for (int k = 0; k < nFlange; k++)
                    {
                        double y = k * segLen;
                        // 外轮廓（上下对称，含管孔）
                        var ub = new List<double>(); var vb = new List<double>();
                        for (double x = g.TabTipXMm; x <= g.DiscRadiusMm; x += 2)
                        { var q = Proj(x, y, g.HalfWidth(x)); ub.Add(q.u); vb.Add(q.v); }
                        for (double x = g.DiscRadiusMm; x >= g.TabTipXMm; x -= 2)
                        { var q = Proj(x, y, -g.HalfWidth(x)); ub.Add(q.u); vb.Add(q.v); }
                        var so = plot.Add.Scatter(ub.ToArray(), vb.ToArray());
                        so.Color = ScottPlot.Colors.Green; so.LineWidth = 1.8f; so.MarkerSize = 0;
                        if (k == 0) so.LegendText = $"法兰（{nFlange} 片，平板壳）";

                        // 内部网格线：每 12 mm 一条
                        for (double x = Math.Ceiling(g.TabTipXMm / 12) * 12; x <= g.DiscRadiusMm; x += 12)
                        {
                            double hw = g.HalfWidth(x); if (hw <= 0) continue;
                            double hole = Math.Abs(x) <= g.HoleRadiusMm
                                ? Math.Sqrt(g.HoleRadiusMm * g.HoleRadiusMm - x * x) : 0;
                            foreach (int sgn in new[] { 1, -1 })
                            {
                                var qa = Proj(x, y, sgn * hole); var qb = Proj(x, y, sgn * hw);
                                var sl = plot.Add.Scatter(new[] { qa.u, qb.u }, new[] { qa.v, qb.v });
                                sl.Color = ScottPlot.Colors.Green.WithAlpha(0.30);
                                sl.LineWidth = 0.6f; sl.MarkerSize = 0;
                            }
                        }
                        // 管孔
                        var uh = new double[73]; var vh = new double[73];
                        for (int t = 0; t <= 72; t++)
                        {
                            double th = 2 * Math.PI * t / 72;
                            var q = Proj(g.HoleRadiusMm * Math.Cos(th), y, g.HoleRadiusMm * Math.Sin(th));
                            uh[t] = q.u; vh[t] = q.v;
                        }
                        var sh = plot.Add.Scatter(uh, vh);
                        sh.Color = ScottPlot.Colors.Red; sh.LineWidth = 1.4f; sh.MarkerSize = 0;
                        if (k == 0) sh.LegendText = "管孔 = 管↔法兰 电流/热流交界";

                        var lbl = plot.Add.Text(k == 0 ? "入口" : k == nFlange - 1 ? "出口" : $"共用{k}",
                                                Proj(g.DiscRadiusMm, y, g.DiscRadiusMm + 8).u,
                                                Proj(g.DiscRadiusMm, y, g.DiscRadiusMm + 8).v);
                        lbl.LabelFontSize = 11; lbl.LabelFontName = "Microsoft YaHei";
                    }
                    for (int k = 0; k < nSeg; k++)
                    {
                        var q = Proj(0, k * segLen + segLen * 0.5, -rOut - 30);
                        var t = plot.Add.Text($"HC{k + 1}", q.u, q.v);
                        t.LabelFontSize = 12; t.LabelFontName = "Microsoft YaHei";
                    }

                    plot.Title($"三维装配壳网格：{nSeg} 段铂金管 + {nFlange} 片法兰（斜投影 Y→右 Z→上 X→斜后）");
                    plot.XLabel("投影 u [mm]"); plot.YLabel("投影 v [mm]");
                    plot.ShowLegend();
                    plot.Axes.AutoScale();
                    fp.Plot.SavePng(Path.Combine(dir, "mesh_3d.png"), 1600, 800);
                    Console.WriteLine($"  → {dir}/mesh_3d.png");
                }

                // ── ① 整线布置
                {
                    var fp = UI.FieldPlots.NewPlot();
                    var plot = fp.Plot;
                    double rOut = p.TubeIdMm * 0.5 + p.WallMinMm, rIn = p.TubeIdMm * 0.5;
                    double yTot = nSeg * segLen;

                    // 管壁（上下两条）
                    foreach (double rr in new[] { rOut, rIn, -rIn, -rOut })
                    {
                        var s = plot.Add.Scatter(new[] { 0.0, yTot }, new[] { rr, rr });
                        s.Color = ScottPlot.Colors.Gray; s.LineWidth = 1.4f; s.MarkerSize = 0;
                    }
                    // 轴向节点（每段抽样画，401 个点画满会糊成一片）
                    for (int k = 0; k < nSeg; k++)
                    {
                        int step = Math.Max(1, p.Nodes / 40);
                        var xs = new List<double>(); var ys = new List<double>();
                        for (int i = 0; i < p.Nodes; i += step)
                        { xs.Add(k * segLen + i * segLen / (p.Nodes - 1)); ys.Add(0); }
                        var s = plot.Add.Scatter(xs.ToArray(), ys.ToArray());
                        s.Color = ScottPlot.Colors.SteelBlue; s.LineWidth = 0;
                        s.MarkerSize = 4; s.MarkerShape = ScottPlot.MarkerShape.FilledCircle;
                        if (k == 0) s.LegendText = $"管轴向节点（每段 {p.Nodes} 个，图中每 {step} 个画 1 个）";
                    }
                    // 法兰平面
                    for (int k = 0; k < nFlange; k++)
                    {
                        double y = k * segLen;
                        var s = plot.Add.Scatter(new[] { y, y },
                                                 new[] { -g.DiscRadiusMm, g.DiscRadiusMm });
                        s.Color = ScottPlot.Colors.Green; s.LineWidth = 5f; s.MarkerSize = 0;
                        if (k == 0) s.LegendText = $"法兰平面（{nFlange} 片，二维网格）";
                        var t = plot.Add.Text(k == 0 ? "入口" : k == nFlange - 1 ? "出口" : $"共用{k}",
                                              y, g.DiscRadiusMm + 6);
                        t.LabelFontSize = 11; t.LabelFontName = "Microsoft YaHei";
                    }
                    for (int k = 0; k < nSeg; k++)
                    {
                        var t = plot.Add.Text($"HC{k + 1}", k * segLen + segLen * 0.5, -g.DiscRadiusMm - 12);
                        t.LabelFontSize = 12; t.LabelFontName = "Microsoft YaHei";
                    }
                    plot.Title($"整线离散布置：{nSeg} 段管（一维轴向）+ {nFlange} 片法兰（二维平面）");
                    plot.XLabel("沿管轴 y [mm]");
                    plot.YLabel("半径 r [mm]");
                    plot.Axes.SetLimits(-40, nSeg * segLen + 40, -g.DiscRadiusMm - 25, g.DiscRadiusMm + 20);
                    plot.ShowLegend();
                    fp.Plot.SavePng(Path.Combine(dir, "mesh_line.png"), 1400, 520);
                    Console.WriteLine($"  → {dir}/mesh_line.png");
                }

                // ── ② 法兰二维网格
                {
                    var f = PlateCurrent2D.Solve(g, 1000.0, Materials.PtResistivity(p.TSetC), h);
                    int nx = f.Nx, nz = f.Nz, inMask = 0;
                    var data = new double[nz, nx];
                    for (int j = 0; j < nz; j++)
                        for (int i = 0; i < nx; i++)
                        {
                            bool m = f.Mask[i, nz - 1 - j];
                            data[j, i] = m ? 1.0 : double.NaN;
                            if (j == 0) { }
                        }
                    foreach (var m in f.Mask) if (m) inMask++;

                    var fp = UI.FieldPlots.NewPlot();
                    var plot = fp.Plot;
                    var hm = plot.Add.Heatmap(data);
                    hm.Extent = new ScottPlot.CoordinateRect(f.X0, f.X0 + (nx - 1) * h,
                                                            f.Z0, f.Z0 + (nz - 1) * h);
                    hm.Smooth = false;                      // 关平滑才看得见格子
                    hm.Colormap = new ScottPlot.Colormaps.Grayscale();

                    // 网格线：每 10 格画一条，画满会糊
                    for (double x = Math.Ceiling(f.X0 / 10) * 10; x <= f.X0 + (nx - 1) * h; x += 10)
                    {
                        var s = plot.Add.Scatter(new[] { x, x }, new[] { f.Z0, f.Z0 + (nz - 1) * h });
                        s.Color = ScottPlot.Colors.LightBlue.WithAlpha(0.35); s.LineWidth = 0.6f; s.MarkerSize = 0;
                    }
                    for (double z = Math.Ceiling(f.Z0 / 10) * 10; z <= f.Z0 + (nz - 1) * h; z += 10)
                    {
                        var s = plot.Add.Scatter(new[] { f.X0, f.X0 + (nx - 1) * h }, new[] { z, z });
                        s.Color = ScottPlot.Colors.LightBlue.WithAlpha(0.35); s.LineWidth = 0.6f; s.MarkerSize = 0;
                    }
                    // 管孔
                    int n = 181; var cx = new double[n]; var cz = new double[n];
                    for (int k = 0; k < n; k++)
                    {
                        double a = 2 * Math.PI * k / (n - 1);
                        cx[k] = g.HoleRadiusMm * Math.Cos(a); cz[k] = g.HoleRadiusMm * Math.Sin(a);
                    }
                    var hole = plot.Add.Scatter(cx, cz);
                    hole.Color = ScottPlot.Colors.Red; hole.LineWidth = 2f; hole.MarkerSize = 0;
                    hole.LegendText = "管孔 Ø52（电流交给管壁处，J 峰值）";

                    plot.Title($"法兰平面网格：{nx}×{nz} 格，步长 {h:0.0} mm，掩膜内 {inMask} 格参与求解");
                    plot.XLabel("x [mm]（0 = 管轴，负向为舌片）");
                    plot.YLabel("z [mm]");
                    plot.Axes.SetLimits(f.X0, f.X0 + (nx - 1) * h, f.Z0, f.Z0 + (nz - 1) * h);
                    plot.ShowLegend();
                    fp.Plot.SavePng(Path.Combine(dir, "mesh_flange.png"), 1200, 700);
                    Console.WriteLine($"  → {dir}/mesh_flange.png   ({nx}×{nz}，掩膜内 {inMask} 格)");
                }
                return;
            }

            // --cli --insulfit [圆盘半径]  扫保温厚度 → Φ=1 处的 J
            //
            // 这是唯一**不花铂金**的杠杆。机理：J|_{Φ=1} 由「法兰要发多少热才能自给」决定，
            // 而那取决于法兰散多少热 ⇒ 保温越厚，需要的自身发热越少，法兰可越厚，J 越低。
            // 已知两点：纤维 10 mm（旧假设）时 J≈12.9；2.5 mm（实况）时 J≈16–24。
            // 管与法兰按同一包覆工艺同步加厚（现场就是同一种纤维同一道工序）。
            if (args.Contains("--insulfit"))
            {
                int ii = Array.IndexOf(args, "--insulfit");
                double ro = ii + 1 < args.Length && double.TryParse(args[ii + 1], out var rv2) ? rv2 : 60.0;
                double tset = 1050, tglass = 1130, head = 1.0, clamp = 80, wall = 1.0;

                Console.WriteLine("=== 保温厚度 → Φ=1 处的电流密度（不花铂金的杠杆）===");
                Console.WriteLine($"最不利段 HC3：控温 {tset:0} °C，玻璃 {tglass:0} °C，铜排夹持 {clamp:0} °C");
                Console.WriteLine($"圆盘半径 {ro:0.0} mm（Ø{2 * ro:0}），管壁 {wall:0.00} mm");
                Console.WriteLine("管与法兰保温同步加厚（同一种纤维、同一道工序）；对每档二分法兰厚求温差 = 0");
                Console.WriteLine();
                Console.WriteLine($"{"纤维 mm",9}{"法兰厚 mm",11}{"温差 K",9}{"法兰 J",9}{"抽热 W/片",11}" +
                                  $"{"段功率 W",10}{"两片铂重 g",12}  判定");

                foreach (double ins in new[] { 2.5, 4.0, 6.0, 8.0, 10.0, 15.0, 20.0 })
                {
                    (double d, double jf, double dr, double pw, double m, bool ok) Probe(double ft)
                    {
                        var q = SegmentSolver.Clone(p);
                        q.TSetC = tset; q.TGlassInC = tglass; q.GlassHeadM = head;
                        q.WallMinMm = wall; q.SizeWall = false;
                        q.SizeFlangeThickness = false; q.BusbarClampTempC = clamp;
                        q.Layer1.ThicknessMm = ins;          // 管的纤维
                        q.FlangeInsulThickMm = ins;          // 法兰的纤维，同工艺
                        try
                        {
                            var c = CoupledSolver.Solve(q,
                                new FlangePlate { DiscRadiusMm = ro, ThicknessMm = ft, ThickenedMm = ft });
                            return c.Tube.Ok
                                ? (tset - c.Tube.TFlangeAC, c.JFlangeMaxAPerMm2, c.FlangeDrawW,
                                   c.Tube.PowerTotalW, c.MassFlangePairG, true)
                                : (0, 0, 0, 0, 0, false);
                        }
                        catch { return (0, 0, 0, 0, 0, false); }
                    }

                    // ★ 靶为 RootDeltaTargetK 而非 0（§4.2k）
                    double Dev(double d) => d - RootDeltaTargetK;
                    double lo = 0.4, hi = 8.0;
                    var fLo = Probe(lo); var fHi = Probe(hi);
                    if (!fLo.ok || !fHi.ok) { Console.WriteLine($"{ins,9:0.0}   ✗ 端点求解失败"); continue; }
                    if (Dev(fLo.d) * Dev(fHi.d) > 0)
                    { Console.WriteLine($"{ins,9:0.0}   ✗ [{lo:0.0},{hi:0.0}] 内未跨过靶值 {RootDeltaTargetK:0.#} K" +
                                        $"（{fLo.d:+0.0;-0.0} … {fHi.d:+0.0;-0.0} K）"); continue; }
                    for (int k = 0; k < 8; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        var f = Probe(mid);
                        if (!f.ok) break;
                        if (Dev(f.d) * Dev(fLo.d) > 0) { lo = mid; fLo = f; } else hi = mid;
                    }
                    double t2 = 0.5 * (lo + hi);
                    var r2 = Probe(t2);
                    string v = (RootDeltaOk(r2.d) ? "✓温差" : r2.d <= 0 ? "★倒灌Φ>1" : "✗温差")
                             + (r2.jf <= p.JAllowAPerMm2 ? "  ✓J" : "  ·J 超参考值");
                    Console.WriteLine($"{ins,9:0.0}{t2,11:0.000}{r2.d,9:+0.0;-0.0}{r2.jf,9:0.00}" +
                                      $"{r2.dr,11:0.0}{r2.pw,10:0}{r2.m,12:0}  {v}");
                }
                Console.WriteLine();
                Console.WriteLine("若某档同时 ✓温差 ✓J，则「≤10 K」不用多花一克铂金，只需加厚保温。");
                Console.WriteLine("同时看段功率：保温加厚也直接省电，且降低升温所需功率。");
                return;
            }

            // --cli --shell [file.3dm] [图层] [平面Y]   变步长壳网格上解电流场（路线 A 闭环）
            //
            // 不给 .3dm 时用解析几何（旧法兰），并与现有 PlateCurrent2D **做回归**：
            // 同一物理、不同网格，J_max 与守恒误差应当在网格收敛容差内一致。
            // 给 .3dm 时走厚度场 —— 开槽/阶梯/任意形状皆可，这是新法兰设计的评估入口。
            if (args.Contains("--shell"))
            {
                int si2 = Array.IndexOf(args, "--shell");
                string? f3dm = si2 + 1 < args.Length && !args[si2 + 1].StartsWith("--") ? args[si2 + 1] : null;
                string layer = si2 + 2 < args.Length && !args[si2 + 2].StartsWith("--") ? args[si2 + 2] : "法兰";
                double planeY = si2 + 3 < args.Length && double.TryParse(args[si2 + 3], out var py) ? py : double.NaN;

                var g = new FlangePlate();
                double current = 1000.0;                 // 定标电流，与 PlateCurrent2D 回归口径一致
                ShellMesh mesh;

                Console.WriteLine("=== 变步长壳网格上的电流场（路线 A）===");
                if (f3dm is null)
                {
                    Console.WriteLine("几何：解析（圆盘 Ø120 + 梯形舌片，等厚 2.0 mm）—— 与 PlateCurrent2D 回归");
                    mesh = FlangeMesher.Build(g, 0, hFine: 2.0, hCoarse: 11.0, fineRadius: 45.0);
                }
                else
                {
                    Console.WriteLine($"几何：{f3dm}  图层「{layer}」  平面 Y={(double.IsNaN(planeY) ? "自动" : planeY.ToString("0.0"))}");
                    Console.WriteLine("提取厚度场中（逐点射线，约 1 分钟）…");
                    var fld = Geometry3dm.MeasureThickness(f3dm, layer, planeY, 1.0);
                    Console.WriteLine($"  厚度场 {fld.Nx}×{fld.Nz}  净面积 {fld.AreaMm2:0.0} mm²  " +
                                      $"体积 {fld.VolumeMm3:0.0} mm³ → 单片 {fld.VolumeMm3 * Materials.PtDensity * 1e-6:0.0} g");
                    mesh = FlangeMesher.BuildFromField(fld, g.HoleRadiusMm, 0,
                                                       hFine: 2.0, hCoarse: 11.0, fineRadius: 50.0);
                }

                var (fi2, fb2) = mesh.FaceCounts();
                Console.WriteLine($"网格：{mesh.CellCount} 单元，内部面 {fi2} / 边界面 {fb2}");
                Console.WriteLine($"      净面积 {mesh.TotalArea:0.000} mm²   体积 {mesh.VolumeMm3:0.0} mm³" +
                                  $" → 单片 {mesh.VolumeMm3 * Materials.PtDensity * 1e-6:0.0} g");

                var sc = ShellCurrent.Solve(mesh, current,
                                            Materials.PtResistivity(p.TSetC) * 1000.0, p.TSetC);
                Console.WriteLine();
                Console.WriteLine($"求解：{sc.Iterations} 次迭代，残差 {sc.Residual:E2}");
                Console.WriteLine($"  电流守恒误差 {sc.ConservationError:E3}   " +
                                  $"{(sc.ConservationError < 5e-3 ? "✓" : "✗ 偏大")}");
                Console.WriteLine($"  J_max {sc.JMaxAPerMm2:0.000} A/mm²   J_mean {sc.JMeanAPerMm2:0.000}" +
                                  $"   @{current:0} A");
                if (sc.JMaxCell >= 0)
                {
                    var c = mesh.Centroid[sc.JMaxCell];
                    Console.WriteLine($"  J_max 位置 (x={c.X:0.0}, z={c.Z:0.0})  r={Math.Sqrt(c.X * c.X + c.Z * c.Z):0.0} mm" +
                                      $"  该处厚度 {mesh.Thickness[sc.JMaxCell]:0.00} mm");
                }
                Console.WriteLine($"  整片焦耳热 {sc.TotalGenW:0.0} W @{current:0} A");

                // ── 温度场：J 已知，解 ∇·(k t ∇T) + q_v·t − 2q″(T) = 0
                //    这是判断「局部高 J 会不会真出问题」的唯一途径 ——
                //    电场只说 J 有多高，温度场才说辐条会不会烧红、冷点消没消。
                double iSeg = 1446;      // HC3 实际段电流（--lineopt 实算）
                var jScaled = sc.JMagAPerMm2.Select(v => v * iSeg / current).ToArray();
                double tRoot = 1050;     // HC3 控温点，作管孔定温
                var th = ShellThermal.Solve(mesh, jScaled, p, tRoot, g.InsulBoundaryXResolved);

                Console.WriteLine();
                Console.WriteLine($"=== 温度场（按 HC3 实际段电流 {iSeg:0} A 定标，管根定温 {tRoot:0} °C）===");
                Console.WriteLine($"  {th.Iterations} 次迭代，残差 {th.Residual:E2}  " +
                                  $"{(th.Converged ? "✓ 收敛" : "✗ 未收敛")}");
                Console.WriteLine($"  J_max（实际电流下）{sc.JMaxAPerMm2 * iSeg / current:0.00} A/mm²");
                Console.WriteLine($"  温度 最高 {th.TMaxC:0.0} / 最低 {th.TMinC:0.0} °C");
                Console.WriteLine($"  自身发热 {th.QGenW:0.0} W   表面散热 {th.QLossW:0.0} W   " +
                                  $"自给率 Φ = {th.PhiOverall:0.000}");
                Console.WriteLine($"  从管子抽热 {th.QFromTubeW:+0.0;-0.0} W/片" +
                                  $"（>0 抽热造冷点，<0 倒灌造热点）");
                Console.WriteLine($"  舌片末端均温 {th.TTabEndMeanC:0.0} °C");
                double over = th.TMaxC - tRoot;
                Console.WriteLine($"  ★ 最高温比管根高 {over:+0.0;-0.0} K" +
                                  $"{(th.TMaxC > 1400 ? "  ✗ 局部过热（>1400 °C）" : "  ✓ 未见局部过热")}");

                // 场图：电流密度 + 厚度分布 + 温度（用户要求每次输出都给二维分布）
                try
                {
                    Directory.CreateDirectory("figs");
                    ApplicationConfiguration.Initialize();
                    string tag = f3dm is null ? "shell_old" : "shell_new";
                    void SaveShell(string suffix, Action<ScottPlot.WinForms.FormsPlot> draw)
                    {
                        var fp2 = UI.FieldPlots.NewPlot();
                        draw(fp2);
                        fp2.Plot.SavePng(Path.Combine("figs", $"{tag}_{suffix}.png"), 1300, 720);
                    }
                    SaveShell("J", f2 => UI.FieldPlots.DrawShellField(f2, mesh, sc.JMagAPerMm2,
                        $"电流密度 @{current:0} A", "J", "A/mm²", g.HoleRadiusMm));
                    SaveShell("t", f2 => UI.FieldPlots.DrawShellField(f2, mesh, mesh.Thickness.ToArray(),
                        "板厚分布", "t", "mm", g.HoleRadiusMm, 1.0));
                    SaveShell("T", f2 => UI.FieldPlots.DrawShellField(f2, mesh, th.T,
                        $"温度场 @{iSeg:0} A", "T", "°C", g.HoleRadiusMm, 1.0));
                    Console.WriteLine($"  → figs/{tag}_J.png, {tag}_t.png, {tag}_T.png");
                }
                catch (Exception ex) { Console.WriteLine("  ⚠ 场图失败：" + ex.Message); }

                if (f3dm is null)
                {
                    var old = PlateCurrent2D.Solve(g, current, Materials.PtResistivity(p.TSetC), 1.0);
                    Console.WriteLine();
                    Console.WriteLine("=== 与 PlateCurrent2D 回归（同一几何、同一电流）===");
                    Console.WriteLine($"{"量",-16}{"壳网格",14}{"PlateCurrent2D",16}{"相对差",12}");
                    void Cmp(string name, double a, double b)
                        => Console.WriteLine($"{name,-16}{a,14:0.0000}{b,16:0.0000}" +
                                             $"{(b != 0 ? (a - b) / b * 100 : double.NaN),11:+0.0;-0.0}%");
                    Cmp("J_max A/mm²", sc.JMaxAPerMm2, old.JMaxAPerMm2);
                    Cmp("J_mean A/mm²", sc.JMeanAPerMm2, old.JMeanAPerMm2);
                    Cmp("守恒误差", sc.ConservationError, old.ConservationError);
                    Console.WriteLine("J_max 有 ±几 % 的网格敏感性（HANDOVER §6 记为 +3.5%），");
                    Console.WriteLine("故 J_mean 与守恒误差是更可靠的回归指标。");
                }
                return;
            }

            // --cli --run [法兰.3dm]   整线求解（LineRunner 唯一入口，CLI 与 UI 共用）
            if (args.Contains("--run"))
            {
                int ri2 = Array.IndexOf(args, "--run");
                // 默认用**现役几何** Pt_Heater.3dm（--geom 校核过 11 项 0.00 %）。
                // 曾默认 Pt_Heater2.3dm（新开槽阶梯法兰），但那个设计已判电气不可行
                // （辐条把导流截面掐到管子的 30 %，J≈62，见 14333a9 的提交说明）——
                // 拿它当默认会让每次 --run 都在算一个已经否掉的方案。
                string f3dm = ri2 + 1 < args.Length && !args[ri2 + 1].StartsWith("--")
                              ? args[ri2 + 1] : Find3dm("Pt_Heater.3dm");
                bool measured = !args.Contains("--solve");   // 默认实测电流；--solve 切到反算

                var lc = new LineCase
                {
                    Base = p,
                    UseMeasuredCurrent = measured,
                    FlangeFile3dm = new[] { f3dm },
                    FlangeLayer = "法兰"
                };

                Console.WriteLine("=== 整线求解（LineRunner）===");
                Console.WriteLine($"法兰几何 {Path.GetFileName(f3dm)}   " +
                                  $"电流模式 {(measured ? "实测" : "由控温反算")}");
                Console.WriteLine($"{lc.SegmentCount} 段 + {lc.FlangeCount} 片");
                Console.WriteLine();

                var prog = new SyncProgress<string>(s => Console.WriteLine("  … " + s));
                LineResult lr;
                try { lr = LineRunner.Run(lc, prog); }
                catch (Exception ex) { Console.WriteLine("✗ " + ex.Message); return; }
                if (!lr.Ok) { Console.WriteLine("✗ " + lr.Message); return; }

                Console.WriteLine();
                Console.WriteLine($"{"段",6}{"控温",7}{"电流 A",9}{"功率 W",9}{"管 J",8}" +
                                  $"{"管根 °C",10}{"衔接温差 K",12}{"玻璃出 °C",11}{"管重 g",9}");
                foreach (var s in lr.Segments)
                    Console.WriteLine($"{s.Name,6}{s.SetpointC,7:0}{s.CurrentA,9:0}{s.PowerW,9:0}" +
                        $"{s.TubeJAPerMm2,8:0.00}{s.TRootC,10:0.0}{s.RootDeltaK,12:+0.0;-0.0}" +
                        $"{s.GlassOutC,11:0.0}{s.MassG,9:0}");

                Console.WriteLine();
                Console.WriteLine($"{"法兰",12}{"共用",6}{"电流 A",9}{"J_max",9}{"Φ",8}" +
                                  $"{"抽热 W",9}{"最高 °C",10}{"舌端 °C",10}{"单元",7}{"铂重 g",9}");
                foreach (var f in lr.Flanges)
                    Console.WriteLine($"{f.Name,12}{(f.Shared ? "是" : "—"),6}{f.CurrentA,9:0}" +
                        $"{f.JMaxAPerMm2,9:0.00}{f.Phi,8:0.000}{f.QFromTubeW,9:+0;-0}" +
                        $"{f.TMaxC,10:0.0}{f.TTabEndC,10:0.0}{f.CellCount,7}{f.MassG,9:0}");

                Console.WriteLine();
                if (!lr.Converged)
                {
                    Console.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
                    Console.WriteLine("║ ★ 段↔法兰耦合未收敛 —— 下面每一个数都不可引用，判定表同样无效。  ║");
                    Console.WriteLine("║   降 LineCase.CoupleRelax 或加 CoupleMaxRounds 再试；            ║");
                    Console.WriteLine("║   若仍不收敛，才是该工况真的热失控（§4.2k 的倒灌正反馈）。       ║");
                    Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");
                }
                Console.WriteLine("判据体系（HANDOVER §4.2k）：★ = 硬安全线，越界即失效；" +
                                  "○ = 设计目标；· = 参考量，只报数不判");
                Console.WriteLine($"{"约束",22}{"实际",12}{"限值",12}  判定  卡在");
                foreach (var k in lr.Checks)
                {
                    string mark = k.Kind switch
                    {
                        CheckKind.HardSafety => "★",
                        CheckKind.Target => "○",
                        _ => "·"
                    };
                    string verdict = k.Kind == CheckKind.Reference ? "—"
                                   : k.Undetermined ? "?"
                                   : k.Ok ? "✓" : "✗";
                    string act = double.IsNaN(k.Actual) ? "达不到" : k.Actual.ToString("0.000");
                    Console.WriteLine($"{mark + k.Name,22}{act,12}{k.Limit,12:0.000}" +
                                      $"  {verdict}   {k.Where}  [{k.Unit}]");
                    if (k.Note.Length > 0) Console.WriteLine($"{"",22}  {k.Note}");
                }

                Console.WriteLine();
                Console.WriteLine($"铂重：管 {lr.TubeMassG:0} + 法兰 {lr.FlangeMassG:0} = " +
                                  $"{lr.TotalMassG:0} g   基准 {lr.BaselineMassG:0} g   " +
                                  $"省铂 {lr.SavingPct:+0.0;-0.0} %");
                Console.WriteLine($"玻璃温降：模型 {lr.GlassDropModelK:0.0} K   实测 {lr.GlassDropMeasuredK:0.0} K");
                foreach (var nte in lr.Notes) Console.WriteLine("  ⚠ " + nte);
                return;
            }

            // --cli --calib [法兰.3dm] [--i I1[,I2,I3]] [--scan]
            //
            // §4.2l 的反标定：用现场实测量反解「散热标定系数」LossScale。
            //
            //   ① 无参数        → 靶为**实测玻璃温降**（现场至今唯一给过的量，20 K）
            //   ② --i 1200,…   → 靶为**实测段电流**（§4.2l 首选，一个点即可）
            //   ③ --scan       → 不求根，只把 0.25/0.50/1.00 三档摊开，
            //                     看每个被判定的量随标定怎么动 —— 这决定了标定能救哪些结论
            //
            // 闭式初值（②用）：定温下 P ≈ Q_loss ∝ scale，而 P = I²R 且温度场几乎不变（R 不变）
            //   ⇒ I ∝ √scale ⇒ scale₀ = (I_实测 / I_模型)²。之后割线法收尾。
            if (args.Contains("--calib"))
            {
                int ci = Array.IndexOf(args, "--calib");
                string f3 = ci + 1 < args.Length && !args[ci + 1].StartsWith("--")
                            ? args[ci + 1] : Find3dm("Pt_Heater.3dm");
                bool scanOnly = args.Contains("--scan");

                double[] iMeas = Array.Empty<double>();
                int ii = Array.IndexOf(args, "--i");
                if (ii >= 0 && ii + 1 < args.Length)
                    iMeas = args[ii + 1].Split(',')
                        .Select(s => double.TryParse(s, out var v) ? v : double.NaN)
                        .Where(v => !double.IsNaN(v)).ToArray();

                Console.WriteLine("=== 散热反标定（HANDOVER §4.2l）===");
                Console.WriteLine($"法兰几何 {Path.GetFileName(f3)}；电流由控温点反算（不用那组假‘实测’电流）");
                Console.WriteLine(iMeas.Length > 0
                    ? $"标定靶：实测段电流 {string.Join(" / ", iMeas.Select(v => v.ToString("0")))} A"
                    : "标定靶：实测玻璃温降（LineCase.GlassOutMeasuredC）");
                Console.WriteLine();

                LineCase MakeCase(double scale)
                {
                    var q = SegmentSolver.Clone(p);
                    q.LossScale = scale;
                    return new LineCase
                    {
                        Base = q,
                        UseMeasuredCurrent = false,      // ★ 反算模式：控温点是硬约束，电流是输出
                        FlangeFile3dm = new[] { f3 },
                        FlangeLayer = "法兰",
                        CheckRamp = false                // 标定只关心稳态，省几秒
                    };
                }

                var seen = new List<(double s, double[] amps, double jt, double jf, double phi,
                                     double drop, double rootMin)>();

                (double[] amps, double jt, double jf, double phi, double drop, double rootMin)?
                Eval(double scale)
                {
                    LineResult r;
                    try { r = LineRunner.Run(MakeCase(scale)); }
                    catch (Exception ex) { Console.WriteLine($"  scale={scale:0.000} ✗ {ex.Message}"); return null; }
                    if (!r.Ok) { Console.WriteLine($"  scale={scale:0.000} ✗ {r.Message}"); return null; }

                    if (!r.Converged)
                    {
                        // 不收敛的点不能进标定 —— 拿发散解去反解参数，得到的是噪声的拟合
                        Console.WriteLine($"  scale={scale:0.000} ✗ 段↔法兰耦合未收敛，该点丢弃");
                        return null;
                    }
                    var amps = r.Segments.Select(s => s.CurrentA).ToArray();
                    double jt = r.Segments.Max(s => s.TubeJAPerMm2);
                    double jf = r.Flanges.Max(f => f.JMaxAPerMm2);
                    double phi = r.Flanges.Max(f => f.Phi);
                    double rootMin = r.Segments.Min(s => s.RootDeltaK);
                    var t = (amps, jt, jf, phi, r.GlassDropModelK, rootMin);
                    seen.Add((scale, amps, jt, jf, phi, r.GlassDropModelK, rootMin));
                    Console.WriteLine($"{scale,10:0.0000}{string.Join("/", amps.Select(a => a.ToString("0"))),18}" +
                                      $"{jt,10:0.00}{jf,10:0.00}{phi,9:0.000}{r.GlassDropModelK,12:0.0}{rootMin,12:0.0}");
                    return t;
                }

                Console.WriteLine($"{"LossScale",10}{"段电流 A",18}{"管 J",10}{"法兰 J",10}" +
                                  $"{"Φ_max",9}{"玻璃温降 K",12}{"最小管根温差 K",14}");

                if (scanOnly)
                {
                    foreach (double s in new[] { 1.00, 0.50, 0.25 }) Eval(s);
                }
                else if (iMeas.Length > 0)
                {
                    var b = Eval(1.0);
                    if (b is null) { Console.WriteLine("基准解失败，无法标定"); return; }
                    double Rms(double[] v) => Math.Sqrt(v.Select(x => x * x).Average());
                    double target = Rms(iMeas);
                    // 闭式初值 + 割线收尾（残差用对数，避免量纲敏感）
                    double s0 = 1.0, f0 = Math.Log(Rms(b.Value.amps) / target);
                    double s1 = Math.Pow(target / Rms(b.Value.amps), 2.0);
                    for (int k = 0; k < 5; k++)
                    {
                        var e = Eval(s1);
                        if (e is null) break;
                        double f1 = Math.Log(Rms(e.Value.amps) / target);
                        if (Math.Abs(f1) < 2e-3) break;              // 电流对齐到 0.2 %
                        double ds = f1 * (s1 - s0) / Math.Max(1e-12, f1 - f0);
                        s0 = s1; f0 = f1;
                        s1 = Math.Clamp(s1 - ds, 0.02, 5.0);
                    }
                }
                else
                {
                    var b = Eval(1.0);
                    if (b is null) { Console.WriteLine("基准解失败，无法标定"); return; }
                    double target = new LineCase().GlassInC - new LineCase().GlassOutMeasuredC;
                    double s0 = 1.0, f0 = b.Value.drop - target;
                    double s1 = 0.4;                                  // 起步猜「实际散热约为模型的四成」
                    for (int k = 0; k < 6; k++)
                    {
                        var e = Eval(s1);
                        if (e is null) break;
                        double f1 = e.Value.drop - target;
                        if (Math.Abs(f1) < 0.3) break;                // 温降对齐到 0.3 K
                        double ds = f1 * (s1 - s0) / Math.Max(1e-12, f1 - f0);
                        s0 = s1; f0 = f1;
                        s1 = Math.Clamp(s1 - ds, 0.02, 5.0);
                    }
                }

                Console.WriteLine();
                if (seen.Count >= 2)
                {
                    var a0 = seen[0]; var aN = seen[^1];
                    double rs = aN.s / a0.s;
                    Console.WriteLine("标定的**结构性结论**（与具体靶值无关）：");
                    Console.WriteLine($"  电流   ∝ scale^{Math.Log(Rms2(aN.amps) / Rms2(a0.amps)) / Math.Log(rs):0.00}" +
                                      "   （理论 0.5：定温下 P≈Q_loss∝scale 而 P=I²R）");
                    Console.WriteLine($"  管 J   ∝ scale^{Math.Log(aN.jt / a0.jt) / Math.Log(rs):0.00}" +
                                      $"     {a0.jt:0.00} → {aN.jt:0.00}");
                    Console.WriteLine($"  法兰 J ∝ scale^{Math.Log(aN.jf / a0.jf) / Math.Log(rs):0.00}" +
                                      $"     {a0.jf:0.00} → {aN.jf:0.00}");
                    Console.WriteLine($"  Φ_max  ∝ scale^{Math.Log(aN.phi / a0.phi) / Math.Log(rs):0.00}" +
                                      $"     {a0.phi:0.000} → {aN.phi:0.000}");
                    Console.WriteLine();
                    Console.WriteLine("★ 若 Φ 的指数接近 0，则**标定救不了 Φ>1 的判决**：");
                    Console.WriteLine("  法兰自身发热 ∝ I² ∝ scale，自身散热也 ∝ scale，比值不动。");
                    Console.WriteLine("  ⇒ 标定能救的是 J 一类的量（∝√scale），Φ 那条硬安全线得另找原因");
                    Console.WriteLine("    （舌片铜排夹的抽热？法兰保温比假设的薄？§4.2h 的 √3 偏大？）。");
                }
                Console.WriteLine();
                Console.WriteLine("⚠ 标定值只有在靶是**实测**时才成立。玻璃温降那条靶同时受法兰冷点污染");
                Console.WriteLine("  （§6 ②：冷点越深，玻璃放热越多），故它给出的 scale 是**下界性质**的估计；");
                Console.WriteLine("  实测段电流是干净得多的靶 —— 一个点即可，见 §6 待补数据。");
                return;

                static double Rms2(double[] v) => Math.Sqrt(v.Select(x => x * x).Average());
            }

            // --cli --ramp   规程一：空管升温核算（25 → 1150 °C / 3 h）
            // 给出模型此前完全没有的**壁厚下界**：P_max = J_allow²·A·ρe·L ∝ 壁厚，
            // 减薄的同时也在削减可用功率。稳态解只把 J 当上界，方向相反的下界一条都没有。
            if (args.Contains("--ramp"))
            {
                const double fromC = 25, targetC = 1150, hours = 3.0;

                Console.WriteLine("=== 规程一：空管升温 ===");
                Console.WriteLine($"要求 {fromC:0} → {targetC:0} °C / {hours:0.#} h" +
                                  $"（{(targetC - fromC) / hours:0} K/h），管内无玻璃");
                Console.WriteLine($"许用电流密度 {p.JAllowAPerMm2:0.0} A/mm²   段长 {p.TubeLengthMm:0} mm   " +
                                  $"环境 {p.TAmbC:0} °C");
                Console.WriteLine();

                // 法兰随管一起被加热：质量与散热由一次稳态耦合解给出（约 1 min）
                Console.WriteLine("先跑一次稳态耦合解取法兰质量与散热…");
                // 取法兰的**自身发热**与对应电流（不是它从管子抽的热）——
                // 升温时法兰同样通电发热，只按散热算会把门槛抬得过高。
                double mFlangePairG = 0, genRefW = -1, iRefA = 0;
                try
                {
                    var q0 = SegmentSolver.Clone(p);
                    q0.TSetC = targetC; q0.TGlassInC = targetC;
                    q0.SizeWall = false; q0.SizeFlangeThickness = false;
                    var c0 = CoupledSolver.Solve(q0, new FlangePlate());
                    if (c0.Tube.Ok)
                    { mFlangePairG = c0.MassFlangePairG; genRefW = c0.Flange.QGenW; iRefA = c0.Tube.CurrentA; }
                }
                catch { /* 拿不到就只算管，下面会注明 */ }
                Console.WriteLine(genRefW > 0
                    ? $"  法兰两片 {mFlangePairG:0} g，自身发热 {genRefW:0} W/片 @ {targetC:0} °C / {iRefA:0} A"
                    : "  ⚠ 耦合解未成功，本次只计管本身（升温会被算得偏快）");
                Console.WriteLine();

                Console.WriteLine($"{"壁厚 mm",9}{"截面 mm²",11}{"电流 A",9}{"实际 J",9}" +
                                  $"{"目标处功率 W",14}{"目标处散热 W",14}{"用时 h",9}{"最高 °C",10}" +
                                  $"{"I_stab A",10}  判定");

                foreach (double wmm in new[] { 0.30, 0.40, 0.50, 0.55, 0.60, 0.70, 1.00, 1.50, 2.00 })
                {
                    var rr = RampSolver.Solve(p, wmm, mFlangePairG, genRefW, iRefA, targetC,
                                              fromC, targetC, hours);
                    string verdict = (rr.Reached ? "✓ 达标" : "✗ " + rr.Note)
                                   + (rr.StabilityLimited ? "（电流被热稳定极限压低）" : "");
                    Console.WriteLine($"{wmm,9:0.00}{rr.TubeAreaMm2,11:0.0}{rr.CurrentA,9:0}" +
                        $"{rr.JAPerMm2,9:0.00}{rr.PowerAtTargetW,14:0}{rr.LossAtTargetW,14:0}" +
                        $"{(rr.Reached ? rr.HoursToTarget.ToString("0.00") : "—"),9}" +
                        $"{rr.TPeakC,10:0.0}{rr.IStabA,10:0}  {verdict}");
                }

                double wMin = RampSolver.MinWallForRampMm(p, mFlangePairG, genRefW, iRefA, targetC,
                                                          fromC, targetC, hours);
                Console.WriteLine();
                if (double.IsNaN(wMin))
                    Console.WriteLine("★ 在 6.0 mm 以内没有壁厚能满足升温要求 —— " +
                                      "需提高许用电流密度、加强保温，或放宽升温时间。");
                else
                    Console.WriteLine($"★ 升温要求给出的**最小壁厚下界 = {wMin:0.000} mm**");

                var probe = RampSolver.Solve(p, p.WallMinMm, mFlangePairG, genRefW, iRefA, targetC,
                                             fromC, targetC, hours);
                Console.WriteLine($"  热容分解 @现状壁厚：金属 {probe.CapMetalJPerK:0} J/K + " +
                                  $"保温 {probe.CapInsulJPerK:0} J/K" +
                                  $"（保温占 {probe.CapInsulJPerK / (probe.CapMetalJPerK + probe.CapInsulJPerK) * 100:0}%）");
                Console.WriteLine("  ⚠ 保温层密度与比热目前是典型值而非实测（见 HANDOVER §6 待补数据 ⑤），");
                Console.WriteLine("    升温时间对其敏感 —— 若保温热容占比高，这个下界的可信度就受限于那两个数。");
                return;
            }

            // --cli --line   分段核算（示例三段，UI 里可编辑）
            if (args.Contains("--line"))
            {
                // 稳态实际控温（用户提供）：控温点在每段中点，沿流向**递减** —— 供料管在受控降温。
                // 玻璃入口 1150 → 出口 1130（全程降 20 K），故后两段金属比玻璃冷，是玻璃在加热管子。
                // 注意：1150 同时是**工作温度上限**，强度/蠕变按它校核；升温工况亦以 1150 为目标。
                // 水头仍为示例值 —— 待补实测。
                var segs = new List<Segment>
                {
                    new(){ Name="HC1", TSetC=1150, TGlassInC=1150, GlassHeadM=0.3, LengthMm=300, WallMm=1.0 },
                    new(){ Name="HC2", TSetC=1080, TGlassInC=1143, GlassHeadM=0.6, LengthMm=300, WallMm=1.0 },
                    new(){ Name="HC3", TSetC=1050, TGlassInC=1137, GlassHeadM=1.0, LengthMm=300, WallMm=1.0 },
                };
                var cands = new[]{ "Pt","Pt-Rh/90-10","Tanaka-ZGS-Pt","FKS16/Pt",
                                   "Tanaka-ZGS-PtRh10","FKS16/PtRh-9010" };

                void Dump(string title, List<Segment> ss)
                {
                    var rs = LineSolver.Solve(ss, p);
                    var (m, c, bad) = LineSolver.Totals(rs);
                    Console.WriteLine();
                    Console.WriteLine("=== " + title + " ===");
                    Console.WriteLine($"{"段",6}{"温度",7}{"水头",7}{"牌号",20}{"壁厚",8}{"强度最小",10}" +
                                      $"{"σ_vm",8}{"许用",8}{"利用率",8}{"铂重",8}{"相对成本",10}  判定");
                    foreach (var r in rs)
                        Console.WriteLine($"{r.Seg.Name,6}{r.Seg.TSetC,7:0}{r.Seg.GlassHeadM,7:0.0}" +
                            $"{r.Seg.GradeName,20}{r.Seg.WallMm,8:0.000}" +
                            $"{(double.IsNaN(r.MinWallStrengthMm) ? "不可行" : r.MinWallStrengthMm.ToString("0.000")),10}" +
                            $"{r.VonMisesMPa,8:0.000}{r.AllowMPa,8:0.000}{r.Utilization,8:0.00}" +
                            $"{r.MassG,8:0}{r.CostRelative,10:0}  {(r.Feasible ? "✓" : "✗ 强度")}");
                    Console.WriteLine($"合计 管铂重 {m:0} g   相对成本 {c:0}" + (bad > 0 ? $"   ★{bad} 段超限" : ""));
                }

                static List<Segment> Copy(List<Segment> ss) => ss.Select(s => new Segment
                {
                    Name = s.Name, TSetC = s.TSetC, TGlassInC = s.TGlassInC,
                    GlassHeadM = s.GlassHeadM, LengthMm = s.LengthMm,
                    TubeIdMm = s.TubeIdMm, WallMm = s.WallMm,
                    GradeName = s.GradeName, TLiquidusC = s.TLiquidusC
                }).ToList();

                var segsNow = Copy(segs);          // ① 现状，留作法兰对比的基准

                Dump("① 现状：全线纯铂 1.0 mm", segs);

                foreach (var s2 in segs)
                {
                    var rr = LineSolver.Solve(new[]{s2}, p)[0];
                    if (!double.IsNaN(rr.MinWallStrengthMm))
                        s2.WallMm = Math.Round(Math.Max(rr.MinWallStrengthMm, 0.3), 3);
                }
                Dump("② 纯铂 + 按强度取最小壁厚", segs);

                foreach (var s2 in segs)
                {
                    s2.WallMm = 1.0;
                    var b = LineSolver.BestGrade(s2, p, cands);
                    if (!string.IsNullOrEmpty(b)) s2.GradeName = b;
                    var rr = LineSolver.Solve(new[]{s2}, p)[0];
                    if (!double.IsNaN(rr.MinWallStrengthMm))
                        s2.WallMm = Math.Round(Math.Max(rr.MinWallStrengthMm, 0.3), 3);
                }
                Dump("③ 按段选最省牌号 + 最小壁厚", segs);
                Console.WriteLine();
                Console.WriteLine("相对成本 = Σ 质量 × 牌号成本倍数（纯铂 = 1.00；Pt-10Rh = 1.39；弥散强化未含加工溢价）");

                // ── 法兰：n 段 n+1 片，共用片电流 = 两侧平均 × 1.5（《鉑金電氣計算.xlsx》口径）
                //    以上三张表只算管壁。法兰厚度由 J 定而非由强度定，不随管壁减薄同比例变薄，
                //    是省铂率的主要稀释源，必须单列出来。
                var plate = new FlangePlate();
                double FlangeTotal(string title, List<Segment> ss, double tubeG)
                {
                    Console.WriteLine();
                    Console.WriteLine($"=== 法兰：{title}（{ss.Count} 段 → {LineSolver.FlangeCount(ss.Count)} 片）===");
                    // 分钟级：逐步打进度，否则十几分钟没有任何反馈，看着像卡死
                    string last = "";
                    var prog = new SyncProgress<LineSolver.FlangeProgress>(fp =>
                    {
                        string s = fp.ToString();
                        if (s == last) return;
                        last = s;
                        Console.WriteLine("  … " + s);
                    });

                    List<LineSolver.FlangeResult> fs;
                    try { fs = LineSolver.SizeFlanges(ss, p, plate, prog); }
                    catch (Exception ex) { Console.WriteLine("  定尺失败：" + ex.Message); return double.NaN; }

                    Console.WriteLine($"{"接头",14}{"共用",6}{"电流 A",10}{"厚度 mm",10}{"铂重 g",10}  定尺依据");
                    foreach (var f in fs)
                        Console.WriteLine($"{f.Joint,14}{(f.Shared ? "是" : "—"),6}{f.CurrentA,10:0}" +
                                          $"{f.ThicknessMm,10:0.000}{f.MassG,10:0}  {f.SizedBy}");
                    double fg = fs.Sum(x => x.MassG);
                    Console.WriteLine($"  法兰合计 {fg:0} g   管 {tubeG:0} g   " +
                                      $"全线 {tubeG + fg:0} g   法兰占 {fg / (tubeG + fg) * 100:0}%");
                    return fg;
                }

                double tubeNow = LineSolver.Totals(LineSolver.Solve(segsNow, p)).massG;
                double tubeBest = LineSolver.Totals(LineSolver.Solve(segs, p)).massG;
                double fNow = FlangeTotal("① 现状", segsNow, tubeNow);
                double fBest = FlangeTotal("③ 最省方案", segs, tubeBest);

                if (!double.IsNaN(fNow) && !double.IsNaN(fBest))
                {
                    double allNow = tubeNow + fNow, allBest = tubeBest + fBest;
                    Console.WriteLine();
                    Console.WriteLine("=== 全线合计（含法兰）===");
                    Console.WriteLine($"{"",10}{"管 g",10}{"法兰 g",10}{"全线 g",10}{"省铂",10}");
                    Console.WriteLine($"{"① 现状",10}{tubeNow,10:0}{fNow,10:0}{allNow,10:0}{"—",10}");
                    Console.WriteLine($"{"③ 最省",10}{tubeBest,10:0}{fBest,10:0}{allBest,10:0}" +
                                      $"{(allNow - allBest) / allNow * 100,9:0.0}%");
                    Console.WriteLine();
                    Console.WriteLine($"只看管壁省 {(tubeNow - tubeBest) / tubeNow * 100:0.0}%，" +
                                      $"计入法兰后只剩 {(allNow - allBest) / allNow * 100:0.0}% —— " +
                                      "法兰厚度由电流密度定，不随管壁减薄同比例变薄。");
                }
                return;
            }

            // --cli --matrix  材料 × 段温度 → 最小可行壁厚 → 铂重（标出卡住的约束）
            if (args.Contains("--matrix"))
            {
                Console.WriteLine();
                Console.WriteLine($"=== 最小可行壁厚矩阵（J_allow={p.JAllowAPerMm2:0.0} A/mm²，" +
                                  $"寿命 {p.DesignLifeHours:0} h，SF={p.SafetyFactor:0.0}，水头 {p.GlassHeadM:0.00} m）===");
                Console.WriteLine("t_强度 = 强度允许的最小壁厚（解析二分）；t_电流 = J 触限壁厚（耦合解）");
                Console.WriteLine();
                var gm = new FlangePlate();
                var grades = new[]{ "Pt","Pt-Rh/90-10","Tanaka-ZGS-Pt","FKS16/Pt",
                                    "Tanaka-ZGS-PtRh10","FKS16/PtRh-9010" };
                var temps = new[]{ 1100.0, 1150, 1200, 1250, 1300 };

                Console.Write($"{"牌号",20}");
                foreach (var t in temps) Console.Write($"{t,12:0}");
                Console.WriteLine("   ← 段温度 °C");
                Console.WriteLine($"{"",20}" + string.Concat(temps.Select(_ => $"{"t_强度 mm",12}")));
                Console.WriteLine(new string('-', 20 + 12 * temps.Length));
                foreach (var gname in grades)
                {
                    var pp = SegmentSolver.Clone(p); pp.GradeName = gname;
                    Console.Write($"{gname,20}");
                    foreach (var t in temps)
                    {
                        pp.TSetC = t;
                        double tw = Mechanics.MinWallForStrengthMm(pp, gm, t);
                        Console.Write(double.IsNaN(tw) ? $"{"不可行",12}" : $"{tw,12:0.000}");
                    }
                    Console.WriteLine();
                }
                Console.WriteLine();
                Console.WriteLine("—— 许用应力 [MPa] 对照 ——");
                Console.Write($"{"牌号",20}");
                foreach (var t in temps) Console.Write($"{t,12:0}");
                Console.WriteLine();
                foreach (var gname in grades)
                {
                    Console.Write($"{gname,20}");
                    foreach (var t in temps)
                        Console.Write($"{(MaterialDb.Get(gname).InCreepRange(t) ? MaterialDb.Get(gname).AllowableMPa(t, p.DesignLifeHours, p.SafetyFactor).ToString("0.000") : "超范围"),12}");
                    Console.WriteLine();
                }
                return;
            }

            // --cli --matcmp  材料对比（实测数据）：断裂强度 + 电阻率 + 对设计的影响
            if (args.Contains("--matcmp"))
            {
                Console.WriteLine();
                Console.WriteLine("=== 材料对比（数据源：用户实测工作簿）===");
                double tOp = p.TSetC, life = p.DesignLifeHours;
                Console.WriteLine($"工作温度 {tOp:0} °C，设计寿命 {life:0} h，安全系数 {p.SafetyFactor:0.0}");
                Console.WriteLine();
                var names = new[]{ "Pt","FKS16/Pt","Tanaka-ZGS-Pt","Pt-Rh/90-10",
                                   "Umicore-PtRh10","FKS16/PtRh-9010","Tanaka-ZGS-PtRh10","Pt-Rh/80-20" };
                Console.WriteLine($"{"牌号",22}{"ρe",10}{"σ断裂",10}{"σ许用",10}{"相对纯铂",10}{"ρe相对",10}");
                Console.WriteLine($"{"",22}{"μΩ·cm",10}{"MPa",10}{"MPa",10}{"强度",10}{"电阻率",10}");
                Console.WriteLine(new string('-', 74));
                double rhoPt = MaterialDb.Get("Pt").ResistivityOhmM(tOp) * 1e8;
                double sPt = MaterialDb.Get("Pt").RuptureStressMPa(tOp, life);
                foreach (var n in names)
                {
                    var g = MaterialDb.Get(n);
                    double rho = g.ResistivityOhmM(tOp) * 1e8;
                    double sig = g.RuptureStressMPa(tOp, life);
                    Console.WriteLine($"{n,22}{rho,10:0.00}{sig,10:0.00}" +
                        $"{sig / p.SafetyFactor,10:0.00}{sig / sPt,10:0.00}{rho / rhoPt,10:0.00}");
                }
                Console.WriteLine();
                Console.WriteLine("—— 温度敏感性：断裂强度 [MPa] @ 设计寿命 ——");
                Console.Write($"{"牌号",22}");
                foreach (double t in new[]{1000.0,1100,1200,1300,1400}) Console.Write($"{t,10:0}");
                Console.WriteLine();
                foreach (var n in names)
                {
                    Console.Write($"{n,22}");
                    foreach (double t in new[]{1000.0,1100,1200,1300,1400})
                        Console.Write($"{(MaterialDb.Get(n).InCreepRange(t) ? MaterialDb.Get(n).RuptureStressMPa(t, life).ToString("0.00") : "超范围"),10}");
                    Console.WriteLine();
                }
                return;
            }

            // --cli --tabscan  沿舌片逐点力学校核（变宽度 + 变温度）
            if (args.Contains("--tabscan"))
            {
                Console.WriteLine();
                Console.WriteLine("=== 沿舌片逐点力学校核（变宽度简支梁 + 沿程温度）===");
                var gg2 = new FlangePlate { InsulBoundaryXMm = -200.0, ThicknessMm = 2.0 };
                var pp2 = SegmentSolver.Clone(p);
                pp2.FlangeAirVelocityMPerS = 0; pp2.BusbarClampTempC = 250;
                pp2.SizeWall = false; pp2.WallMinMm = 0.6; pp2.SizeFlangeThickness = false;
                var cc = CoupledSolver.Solve(pp2, gg2);
                if (!cc.Tube.Ok) { Console.WriteLine("✗ " + cc.Tube.Message); return; }
                var fl2 = cc.Flange; var cur2 = cc.Current;

                // 温度沿 x 的分布：取该列的最高温（最不利）
                double TabT(double xmm)
                {
                    int i = (int)Math.Round((xmm - cur2.X0) / cur2.H);
                    i = Math.Clamp(i, 0, cur2.Nx - 1);
                    double best = 0;
                    for (int j = 0; j < cur2.Nz; j++)
                        if (cur2.Mask[i, j]) best = Math.Max(best, fl2.T[i, j]);
                    return best > 0 ? best : 900;
                }

                Console.WriteLine($"工作点：I = {cc.Tube.CurrentA:0} A，管根 {cc.Tube.TFlangeAC:0} °C，" +
                                  $"夹持 250 °C，材料 {p.GradeName}，寿命 {p.DesignLifeHours:0} h");
                Console.WriteLine();
                Console.WriteLine($"{"舌片厚",8}{"σ_max",9}{"@x",8}{"@T",8}" +
                                  $"{"利用率max",10}{"@x",8}{"@T",8}  判定");
                Console.WriteLine($"{"mm",8}{"MPa",9}{"mm",8}{"°C",8}{"",10}{"mm",8}{"°C",8}");
                Console.WriteLine(new string('-', 72));
                foreach (double tf in new[] { 2.50, 2.00, 1.60, 1.37, 1.20 })
                {
                    var sc = Mechanics.ScanTab(p, gg2, tf, TabT);
                    Console.WriteLine($"{tf,8:0.00}{sc.sigMaxMPa,9:0.00}{sc.xAtMaxMm,8:0}{sc.tAtMaxC,8:0}" +
                        $"{sc.utilMax,10:0.00}{sc.xAtUtilMaxMm,8:0}{sc.tAtUtilMaxC,8:0}  " +
                        (sc.utilMax <= 1 ? "✓" : "✗ 超限"));
                }
                Console.WriteLine();
                Console.WriteLine("利用率 = σ·安全系数 / 许用应力(当地温度, 设计寿命)");
                return;
            }

            // --cli --creep  持久强度曲线（实测数据库）
            if (args.Contains("--creep"))
            {
                Console.WriteLine();
                Console.WriteLine("=== 铂系高温持久强度（供应商实测，Tanaka / Umicore）===");
                Console.WriteLine("模型 T·log10(σ) = a(T)·log10(t) + b(T)，a、b 为 T(K) 的五次多项式");
                Console.WriteLine("拟合区间外一律显示「超范围」——多项式外推会发散（见理论模型 §5.2.1）");
                Console.WriteLine();
                var mats = new[]{ "Pt","Pt-Rh/90-10","Pt-Rh/80-20",
                                  "Tanaka-ZGS-Pt","Tanaka-ZGS-PtRh10",
                                  "FKS16/Pt","FKS16/PtRh-9010" };
                foreach (double life in new[]{ 100.0, 8760.0, 43800.0 })
                {
                    Console.WriteLine($"—— 寿命 {life:0} h" +
                        (Math.Abs(life - 8760) < 1 ? "（1 年）" :
                         Math.Abs(life - 43800) < 1 ? "（5 年）" : "") + " ——");
                    Console.Write($"{"温度 °C",9}");
                    foreach (var m in mats) Console.Write($"{m,20}");
                    Console.WriteLine();
                    foreach (double tc in new[]{ 1000.0, 1100, 1200, 1300, 1400, 1500 })
                    {
                        Console.Write($"{tc,9:0}");
                        foreach (var m in mats)
                        {
                            var g = MaterialDb.Get(m);
                            Console.Write(g.InCreepRange(tc)
                                ? $"{g.RuptureStressMPa(tc, life),20:0.00}"
                                : $"{"超范围",20}");
                        }
                        Console.WriteLine();
                    }
                    Console.WriteLine();
                }
                Console.WriteLine("单位 MPa（断裂强度，未除安全系数）");
                foreach (var m in mats)
                {
                    var g = MaterialDb.Get(m);
                    Console.WriteLine($"  {m,-20} 有效区间 {g.CreepTMinC:0}–{g.CreepTMaxC:0} °C" +
                                      (g.RangeConfirmed ? "（实测范围）" : "（推定，须向供应商确认）"));
                }
                return;
            }

            // --cli --mech   力学校核：应力 vs 壁厚，反算所需许用应力
            if (args.Contains("--mech"))
            {
                Console.WriteLine();
                Console.WriteLine($"=== 力学校核（安全系数 {p.SafetyFactor:0.0}，本段水头 {p.GlassHeadM:0.00} m，" +
                                  $"支承跨距 {p.SupportSpanMm:0} mm，舌片两端支承）===");
                Console.WriteLine("程序不内置蠕变数据 —— 输出「所需许用应力」，请对照供应商曲线判定");
                Console.WriteLine();
                Console.WriteLine($"材料 {p.GradeName}，寿命 {p.DesignLifeHours:0} h，" +
                                  $"管温 {p.TSetC:0} °C");
                Console.WriteLine($"{"管壁",7}{"法兰厚",8}{"管合成",9}{"管需许用",10}{"管许用",10}{"管利用率",8}" +
                                  $"{"舌片需许用",10}{"舌片许用",10}{"舌片利用率",8}  判定");
                Console.WriteLine($"{"mm",7}{"mm",8}{"MPa",9}{"MPa",10}{"MPa",10}{"",8}" +
                                  $"{"MPa",10}{"MPa",10}{"",8}");
                Console.WriteLine(new string('-', 92));
                var gm = new FlangePlate();
                foreach (var (w, tf) in new[]{ (1.00,2.00), (0.90,1.73), (0.80,1.63),
                                               (0.70,1.53), (0.60,1.41), (0.562,1.37), (0.50,1.29) })
                {
                    var mr = Mechanics.Check(p, w, tf, gm);
                    Mechanics.ApplyAllowable(mr, p, tubeTempC: p.TSetC, tabTempC: p.TSetC);
                    string verdict = mr.TubeUtil <= 1 && mr.TabUtil <= 1 ? "✓"
                                   : mr.TubeUtil > 1 && mr.TabUtil > 1 ? "✗ 管+舌片"
                                   : mr.TubeUtil > 1 ? "✗ 管" : "✗ 舌片";
                    Console.WriteLine($"{w,7:0.000}{tf,8:0.00}{mr.TubeVonMisesMPa,9:0.000}" +
                        $"{mr.TubeReqAllowMPa,10:0.000}{(double.IsNaN(mr.TubeAllowMPa) ? "超范围" : mr.TubeAllowMPa.ToString("0.000")),10}{mr.TubeUtil,8:0.00}" +
                        $"{mr.TabReqAllowMPa,10:0.00}{(double.IsNaN(mr.TabAllowMPa) ? "超范围" : mr.TabAllowMPa.ToString("0.00")),10}{mr.TabUtil,8:0.00}  {verdict}");
                }
                Console.WriteLine();
                Console.WriteLine("—— 压力水头敏感性（管壁 0.562 mm，其余同上）——");
                Console.WriteLine($"{"水头",7}{"内压",9}{"管环向",9}{"管合成",9}{"管需许用",10}");
                Console.WriteLine($"{"m",7}{"kPa",9}{"MPa",9}{"MPa",9}{"MPa",10}");
                foreach (double head in new[] { 0.2, 0.5, 1.0, 1.5, 2.0, 3.0 })
                {
                    var mr = Mechanics.Check(p, 0.562, 1.37, gm, glassHeadOverrideM: head);
                    double pi2 = p.GlassDensity * 9.81 * head / 1000.0;
                    Console.WriteLine($"{head,7:0.0}{pi2,9:0.0}{mr.TubeHoopMPa,9:0.000}" +
                        $"{mr.TubeVonMisesMPa,9:0.000}{mr.TubeReqAllowMPa,10:0.000}");
                }
                Console.WriteLine();
                Console.WriteLine("参考量级（需你确认）：纯铂 1300 °C / 8760 h 持久强度约 0.5–2 MPa；");
                Console.WriteLine("                      ZGS/DPH 强化铂高 10–20 倍；Pt-10Rh 高 3–5 倍。");
                return;
            }

            // --cli --clamp  夹持温度扫描：判定风冷铜排是否足够（免水冷）
            if (args.Contains("--clamp"))
            {
                Console.WriteLine();
                Console.WriteLine("=== 铂-铜夹持点温度扫描（全包保温，管壁 0.6 mm）===");
                Console.WriteLine($"{"夹持温度",9}{"电流",7}{"抽热",8}{"析晶裕度",10}{"法兰厚",8}" +
                                  $"{"总铂",8}  说明");
                Console.WriteLine($"{"°C",9}{"A",7}{"W/片",8}{"K",10}{"mm",8}{"g",8}");
                Console.WriteLine(new string('-', 70));
                foreach (double tc in new[] { -1, 400.0, 300, 250, 200, 150, 80 })
                {
                    var pp = SegmentSolver.Clone(p);
                    pp.FlangeAirVelocityMPerS = 0;
                    pp.BusbarClampTempC = tc;
                    pp.SizeWall = false; pp.WallMinMm = 0.6;
                    pp.SizeFlangeThickness = true;
                    var gg = new FlangePlate { InsulBoundaryXMm = -200.0, ThicknessMm = 2.0,
                                               ThickenRadiusMm = 40.0, ThickenedMm = 3.0 };
                    CoupledResult c;
                    try { c = CoupledSolver.Solve(pp, gg); }
                    catch (Exception ex) { Console.WriteLine($"{tc,9:0}  ✗ {ex.Message}"); continue; }
                    if (!c.Tube.Ok) { Console.WriteLine($"{tc,9:0}  ✗ {c.Tube.Message}"); continue; }
                    var tb = c.Tube;
                    double mTot = tb.MassTubeKg * 1000 + c.MassFlangePairG;
                    string note = tc < 0 ? "自由辐射（无夹冷）"
                                : tc >= 300 ? "大面积风冷铜排即可"
                                : tc >= 150 ? "强化风冷/热管"
                                : "需水冷";
                    Console.WriteLine($"{(tc < 0 ? "无" : tc.ToString("0")),9}{tb.CurrentA,7:0}" +
                        $"{c.FlangeDrawW,8:+0;-0}{tb.DevitMarginMinK,10:+0;-0}" +
                        $"{c.FlangeThickMm,8:0.00}{mTot,8:0}  {note}");
                }
                return;
            }

            // --cli --tdesign  法兰厚度分布反设计
            if (args.Contains("--tdesign"))
            {
                Console.WriteLine();
                Console.WriteLine("=== 法兰厚度分布反设计（J 处处 = 许用值）===");
                var pp = SegmentSolver.Clone(p);
                pp.FlangeAirVelocityMPerS = 0;
                pp.BusbarClampTempC = 250;
                pp.SizeWall = false; pp.WallMinMm = 0.6;
                pp.SizeFlangeThickness = true;
                var gg = new FlangePlate { InsulBoundaryXMm = -200.0, ThicknessMm = 2.0 };
                var c0 = CoupledSolver.Solve(pp, gg);
                if (!c0.Tube.Ok) { Console.WriteLine("✗ " + c0.Tube.Message); return; }
                double I = c0.Tube.CurrentA;
                Console.WriteLine($"工作点：I = {I:0} A，管壁 {c0.Tube.WallDesignMm:0.000} mm，" +
                                  $"均匀法兰厚 {c0.FlangeThickMm:0.00} mm");
                Console.WriteLine();
                Console.WriteLine($"{"t_min",8}{"迭代",6}{"收敛",9}{"t 范围",16}{"t 均值",8}" +
                                  $"{"J_max",8}{"触底面积",10}{"变厚度铂",10}{"均匀铂",9}{"省",7}");
                Console.WriteLine($"{"mm",8}{"",6}{"mm",9}{"mm",16}{"mm",8}{"A/mm²",8}{"%",10}{"g/片",10}{"g/片",9}{"%",7}");
                Console.WriteLine(new string('-', 96));
                foreach (double tmin in new[] { 0.5, 0.6, 0.8 })
                {
                    var gd = new FlangePlate { InsulBoundaryXMm = -200.0, ThicknessMm = 2.0 };
                    var d = FlangeThicknessDesign.Solve(gd, pp, I, tmin,
                                                        tempField: null, h: 1.0);
                    Console.WriteLine($"{tmin,8:0.0}{d.Iterations,6}{d.Delta,9:E1}" +
                        $"{$"{d.TMinMm:0.00} – {d.TMaxMm:0.00}",16}{d.TMeanMm,8:0.00}" +
                        $"{d.JMaxAPerMm2,8:0.00}{d.AreaAtFloorPct,10:0.0}" +
                        $"{d.MassG,10:0}{d.MassUniformG,9:0}" +
                        $"{(d.MassUniformG - d.MassG) / d.MassUniformG * 100,7:0.0}");
                }
                return;
            }

            // --cli --curve <dir>  省铂曲线细扫描 + 出图
            if (args.Contains("--curve"))
            {
                int ci = Array.IndexOf(args, "--curve");
                string dir = ci + 1 < args.Length && !args[ci + 1].StartsWith("--")
                             ? args[ci + 1] : ".";
                Directory.CreateDirectory(dir);
                ApplicationConfiguration.Initialize();

                double[] walls = { 0.45, 0.50, 0.55, 0.60, 0.70, 0.80, 0.90, 1.00, 1.20 };
                var xs = new List<double>(); var mass = new List<double>();
                var marg = new List<double>(); var jt = new List<double>();

                Console.WriteLine();
                Console.WriteLine("=== 省铂曲线细扫描（全包保温 + 水冷夹 80°C，法兰按 J≤许用定尺）===");
                Console.WriteLine($"{"管壁",7}{"电流",7}{"J管",7}{"法兰厚",8}{"析晶裕度",10}" +
                                  $"{"管铂",7}{"法兰铂",8}{"总铂",8}  约束");
                Console.WriteLine($"{"mm",7}{"A",7}{"A/mm²",7}{"mm",8}{"K",10}{"g",7}{"g/对",8}{"g",8}");
                Console.WriteLine(new string('-', 78));
                foreach (double w in walls)
                {
                    var pp = SegmentSolver.Clone(p);
                    pp.FlangeAirVelocityMPerS = 0;
                    pp.BusbarClampTempC = 80;
                    pp.SizeWall = false; pp.WallMinMm = w;
                    pp.SizeFlangeThickness = true;
                    var gg = new FlangePlate { InsulBoundaryXMm = -200.0, ThicknessMm = 2.0,
                                               ThickenRadiusMm = 40.0, ThickenedMm = 3.0 };
                    CoupledResult c;
                    try { c = CoupledSolver.Solve(pp, gg); }
                    catch (Exception ex) { Console.WriteLine($"{w,7:0.00}  ✗ {ex.Message}"); continue; }
                    if (!c.Tube.Ok) { Console.WriteLine($"{w,7:0.00}  ✗ {c.Tube.Message}"); continue; }
                    var tb = c.Tube;
                    double mT = tb.MassTubeKg * 1000, mAll = mT + c.MassFlangePairG;
                    xs.Add(w); mass.Add(mAll); marg.Add(tb.DevitMarginMinK); jt.Add(tb.JActualAPerMm2);
                    string ok = (tb.JActualAPerMm2 <= p.JAllowAPerMm2 ? "" : "J越界 ")
                              + (tb.DevitMarginMinK >= p.DevitMarginK ? "" : "析晶 ");
                    Console.WriteLine($"{w,7:0.00}{tb.CurrentA,7:0}{tb.JActualAPerMm2,7:0.00}" +
                        $"{c.FlangeThickMm,8:0.00}{tb.DevitMarginMinK,10:+0;-0}" +
                        $"{mT,7:0}{c.MassFlangePairG,8:0}{mAll,8:0}  " +
                        (ok == "" ? "✓ 可行" : "✗ " + ok.Trim()));
                }
                if (xs.Count >= 3)
                {
                    double wJ = Interp(xs, jt, p.JAllowAPerMm2);          // J 触限的壁厚
                    double wD = Interp(xs, marg, p.DevitMarginK);         // 析晶触限的壁厚
                    Console.WriteLine();
                    Console.WriteLine($"约束边界：J = {p.JAllowAPerMm2:0.0} 于 t = {wJ:0.000} mm；" +
                                      $"析晶裕度 = {p.DevitMarginK:0} K 于 t = {wD:0.000} mm");
                    Console.WriteLine($"可行下限 t_min = {Math.Max(wJ, wD):0.000} mm");
                    UI.FieldPlots.DrawSavingCurve(Path.Combine(dir, "saving_curve.png"),
                        xs.ToArray(), mass.ToArray(), wJ, wD, p);
                    Console.WriteLine($"  → saving_curve.png");
                }
                return;
            }

            // --cli --save   省铂曲线：热工况 → 功率 → 电流 → 法兰可减薄量 → 铂重
            if (args.Contains("--save"))
            {
                Console.WriteLine();
                Console.WriteLine("=== 省铂曲线（法兰厚度按 J ≤ 许用值自动定尺）===");
                Console.WriteLine($"{"工况",26}{"电流",7}{"管壁",8}{"J管",7}{"法兰厚",8}{"抽热",8}{"析晶裕度",10}" +
                                  $"{"管铂",7}{"法兰铂",8}{"总铂",8}{"省铂",8}");
                Console.WriteLine($"{"",26}{"A",7}{"mm",8}{"A/mm²",7}{"mm",8}{"W/片",8}{"K",10}" +
                                  $"{"g",7}{"g/对",8}{"g",8}{"%",8}");
                Console.WriteLine(new string('-', 103));
                double baseTotal = 0;
                var cases = new (string name, double vel, double insulX, double thick,
                                 bool sizeWall, double wallMin, double clamp)[]
                {
                    ("现状 吹风10m/s 半包",       10.0, -80.0,  2.0, false, 1.0, -1),
                    ("停吹风 全包",                0.0, -200.0, 2.0, false, 1.0, -1),
                    ("全包 + 水冷夹80°C",          0.0, -200.0, 2.0, false, 1.0, 80),
                    ("全包+水冷+管壁下限0.8",      0.0, -200.0, 2.0, true,  0.8, 80),
                    ("全包+水冷+管壁下限0.6",      0.0, -200.0, 2.0, true,  0.6, 80),
                    ("全包+水冷+管壁下限0.5",      0.0, -200.0, 2.0, true,  0.5, 80),
                };
                foreach (var (name, vel, insulX, thick, sizeWall, wallMin, clamp) in cases)
                {
                    var pp = SegmentSolver.Clone(p);
                    pp.FlangeAirVelocityMPerS = vel;
                    pp.SizeWall = sizeWall;
                    pp.WallMinMm = wallMin;
                    pp.BusbarClampTempC = clamp;
                    pp.SizeFlangeThickness = true;
                    var gg = new FlangePlate { InsulBoundaryXMm = insulX, ThicknessMm = thick,
                                               ThickenRadiusMm = 40.0, ThickenedMm = 3.0 };
                    CoupledResult c;
                    try { c = CoupledSolver.Solve(pp, gg); }
                    catch (Exception ex) { Console.WriteLine($"{name,26}   ✗ {ex.Message}"); continue; }
                    if (!c.Tube.Ok) { Console.WriteLine($"{name,26}   ✗ {c.Tube.Message}"); continue; }
                    var tb = c.Tube; var fl = c.Flange;
                    double mTube = tb.MassTubeKg * 1000, mTot = mTube + c.MassFlangePairG;
                    if (baseTotal == 0) baseTotal = mTot;
                    _ = fl;
                    Console.WriteLine(
                        $"{name,26}{tb.CurrentA,7:0}{tb.WallDesignMm,8:0.000}{tb.JActualAPerMm2,7:0.00}" +
                        $"{c.FlangeThickMm,8:0.00}{c.FlangeDrawW,8:+0;-0}{tb.DevitMarginMinK,10:+0;-0}" +
                        $"{mTube,7:0}{c.MassFlangePairG,8:0}{mTot,8:0}" +
                        $"{(baseTotal - mTot) / baseTotal * 100,8:+0.0;-0.0}");
                }
                return;
            }

            // --cli --flare  末端加宽/延长扫描：同时满足析晶与铜排接点温度
            if (args.Contains("--flare"))
            {
                Console.WriteLine();
                Console.WriteLine("=== 末端延长+张开扫描（近端全包保温，延长段裸露散热）===");
                Console.WriteLine($"{"延长",7}{"末端半宽",10}{"末端全宽",10}{"电流",8}{"J管",7}{"J最大",8}" +
                                  $"{"管根",8}{"析晶裕度",10}{"铜排接点",10}{"法兰铂重",10}  判定");
                Console.WriteLine($"{"mm",7}{"mm",10}{"mm",10}{"A",8}{"A/mm²",7}{"A/mm²",8}" +
                                  $"{"°C",8}{"K",10}{"°C",10}{"g/对",10}");
                Console.WriteLine(new string('-', 100));
                foreach (var (ext, hw) in new[]{ (0.0,40.0), (150.0,40.0), (150.0,90.0),
                                                 (150.0,140.0), (200.0,140.0), (200.0,180.0),
                                                 (250.0,180.0), (250.0,230.0) })
                {
                    var pp = SegmentSolver.Clone(p);
                    var gg = new FlangePlate { ExtensionMm = ext, ExtHalfWidthMm = hw,
                                               InsulBoundaryXMm = -200.0,
                                               ThickenRadiusMm = 40.0, ThickenedMm = 3.0 };
                    CoupledResult c;
                    try { c = CoupledSolver.Solve(pp, gg); }
                    catch (Exception ex) { Console.WriteLine($"{ext,7:0}   ✗ {ex.Message}"); continue; }
                    if (!c.Tube.Ok) { Console.WriteLine($"{ext,7:0}   ✗ {c.Tube.Message}"); continue; }
                    var tb = c.Tube; var fl = c.Flange;
                    bool okD = tb.DevitMarginMinK >= p.DevitMarginK;
                    bool okJ = c.JFlangeMaxAPerMm2 <= p.JAllowAPerMm2;
                    bool okT = fl.TTabEndMeanC <= 300;
                    string v = (okD ? "" : "析晶 ") + (okJ ? "" : "J越界 ") + (okT ? "" : "接点烫 ");
                    Console.WriteLine(
                        $"{ext,7:0}{hw,10:0}{2 * hw,10:0}{tb.CurrentA,8:0}{c.JTubeAPerMm2,7:0.00}" +
                        $"{c.JFlangeMaxAPerMm2,8:0.00}{tb.TFlangeAC,8:0.0}{tb.DevitMarginMinK,10:+0;-0}" +
                        $"{fl.TTabEndMeanC,10:0.0}{c.MassFlangePairG,10:0}  " +
                        (v == "" ? "✓✓ 全达标" : "✗ " + v.Trim()));
                }
                return;
            }

            // --cli --thick   孔周局部加厚扫描（治 J 越界）+ 舌端温度（治铜排接点）
            if (args.Contains("--thick"))
            {
                Console.WriteLine();
                Console.WriteLine("=== 孔周局部加厚扫描（全包保温，X_分界 = −200）===");
                Console.WriteLine($"{"加厚半径",9}{"厚度",7}{"J孔周",8}{"J管",7}{"电流",8}" +
                                  $"{"守恒",9}{"加厚区铂重",11}{"总铂增量",10}  判定");
                Console.WriteLine($"{"mm",9}{"mm",7}{"A/mm²",8}{"A/mm²",7}{"A",8}{"",9}{"g",11}{"g",10}");
                Console.WriteLine(new string('-', 82));
                double aT = Math.PI * (26.0 * 26.0 - 25.0 * 25.0);
                double I0 = 1216.0;
                foreach (var (rt, td) in new[]{ (0.0,2.0), (40.0,3.0), (40.0,4.0), (60.0,3.0),
                                                (60.0,4.0), (60.0,5.0), (60.0,6.0) })
                {
                    var gg = new FlangePlate { ThickenRadiusMm = rt, ThickenedMm = td };
                    var fc = PlateCurrent2D.Solve(gg, I0, Materials.PtResistivity(1300), 0.5);
                    double extra = rt > 26 ? Math.PI * (rt * rt - 26 * 26) * (td - 2.0)
                                             * Materials.PtDensity * 1e-6 : 0;
                    string v = fc.JMaxAPerMm2 <= 10.0 ? "✓ 达标" : "✗ 越界";
                    Console.WriteLine(
                        $"{(rt > 26 ? rt.ToString("0") : "无"),9}{(rt > 26 ? td.ToString("0.0") : "2.0"),7}" +
                        $"{fc.JMaxAPerMm2,8:0.00}{I0 / aT,7:0.00}{I0,8:0}" +
                        $"{fc.ConservationError,9:E1}{extra,11:0}{2 * extra,10:0}  {v}"
                        + $"   J_max@ x={fc.JMaxXMm:0.0} z={fc.JMaxZMm:0.0} r={fc.JMaxRMm:0.0}");
                }
                return;
            }

            // --cli --insul   扫描法兰保温分界位置 X
            if (args.Contains("--insul"))
            {
                Console.WriteLine();
                Console.WriteLine("=== 法兰保温分界位置扫描（X ≥ 分界 = 包覆纤维）===");
                Console.WriteLine("舌片自 x=−200(铜排端) 伸到 x=+60(圆盘外缘)；分界越负 = 包得越多");
                Console.WriteLine();
                Console.WriteLine($"{"分界X",7}{"保温占比",9}{"电流",8}{"J管",7}{"J法兰",8}" +
                                  $"{"抽热/片",9}{"Φ",7}{"管根",8}{"析晶裕度",10}{"铜排接点",9}  判定");
                Console.WriteLine($"{"mm",7}{"%",9}{"A",8}{"A/mm²",7}{"A/mm²",8}" +
                                  $"{"W",9}{"",7}{"°C",8}{"K",10}{"°C",9}");
                Console.WriteLine(new string('-', 96));
                double areaAll = CoupledSolver.PlateArea(new FlangePlate());
                foreach (double xb in new[] { -200, -180, -160, -140, -120, -100, -80, -60, -40, -20, 0.0 })
                {
                    var pp = SegmentSolver.Clone(p);
                    var gg = new FlangePlate { InsulBoundaryXMm = xb };
                    CoupledResult c;
                    try { c = CoupledSolver.Solve(pp, gg); }
                    catch (Exception ex)
                    { Console.WriteLine($"{xb,7:0}   ✗ {ex.Message}"); continue; }
                    if (!c.Tube.Ok) { Console.WriteLine($"{xb,7:0}   ✗ {c.Tube.Message}"); continue; }

                    // 保温覆盖面积占比
                    double aIns = 0; const int N = 20001;
                    double x0 = gg.TabEndXMm, dx = (gg.DiscRadiusMm - x0) / (N - 1);
                    for (int k = 0; k < N; k++)
                    {
                        double x = x0 + k * dx, w = dx * (k == 0 || k == N - 1 ? 0.5 : 1.0);
                        double hole = Math.Abs(x) <= gg.HoleRadiusMm
                            ? Math.Sqrt(gg.HoleRadiusMm * gg.HoleRadiusMm - x * x) : 0;
                        if (x >= xb) aIns += 2 * (gg.HalfWidth(x) - hole) * w;
                    }
                    var t = c.Tube; var f = c.Flange;
                    string v = t.DevitMarginMinK >= p.DevitMarginK ? "✓ 达标"
                             : t.DevitMarginMinK > 0 ? "△ 裕度不足" : "✗ 析晶";
                    if (c.JFlangeMaxAPerMm2 > p.JAllowAPerMm2) v += " / J越界";
                    Console.WriteLine(
                        $"{xb,7:0}{aIns / areaAll * 100,9:0.0}{t.CurrentA,8:0}{c.JTubeAPerMm2,7:0.00}" +
                        $"{c.JFlangeMaxAPerMm2,8:0.00}{c.FlangeDrawW,9:0}{f.PhiOverall,7:0.000}" +
                        $"{t.TFlangeAC,8:0.0}{t.DevitMarginMinK,10:+0;-0}{f.TTabEndMeanC,9:0.0}  {v}");
                }
                return;
            }

            // --cli --couple   一维管段 ↔ 二维法兰 耦合
            if (args.Contains("--couple"))
            {
                var gg = new FlangePlate();
                var c = CoupledSolver.Solve(p, gg);
                if (!c.Tube.Ok) { Console.WriteLine("FAIL: " + c.Note); return; }
                var tb = c.Tube; var fl = c.Flange;
                Console.WriteLine();
                Console.WriteLine("=== 耦合求解（Pt_Heater.3dm 实际几何）===");
                Console.WriteLine($"外层迭代 {c.OuterIterations} 次  |ΔD| = {c.OuterDelta:0.00} W  " +
                                  $"{(c.Converged ? "✓ 收敛" : "✗ 未收敛")}");
                Console.WriteLine($"法兰能量残差 {fl.EnergyResidual:E2}   电流场守恒 {c.Current.ConservationError:E2}");
                Console.WriteLine();
                Console.WriteLine($"电流 I        {tb.CurrentA:0} A     段电压 {tb.VoltageV:0.00} V");
                Console.WriteLine($"电流密度      管 {c.JTubeAPerMm2:0.00} / 法兰最大 {c.JFlangeMaxAPerMm2:0.00} A/mm²" +
                                  $"   (许用 {p.JAllowAPerMm2:0.0})");
                Console.WriteLine($"管段功率      {tb.PowerTotalW:0} W    法兰发热 {fl.QGenW:0} W ×2");
                Console.WriteLine($"法兰抽热      {c.FlangeDrawW:0} W/片   两片 {2 * c.FlangeDrawW:0} W   Φ={fl.PhiOverall:0.000}");
                Console.WriteLine();
                Console.WriteLine($"管温 中/最冷  {tb.TMaxC:0.0} / {tb.TMinC:0.0} °C   管根 {tb.TFlangeAC:0.0} °C");
                Console.WriteLine($"法兰温度      保温段最低 {(double.IsNaN(fl.TMinInsulC) ? "—" : fl.TMinInsulC.ToString("0.0"))}" + $" / 裸露段最低 {(double.IsNaN(fl.TMinBareC) ? "—" : fl.TMinBareC.ToString("0.0"))} °C");
                Console.WriteLine($"铜排压接点    {fl.TTabEndMinC:0.0} ~ {fl.TTabEndMaxC:0.0} °C  (均 {fl.TTabEndMeanC:0.0})");
                Console.WriteLine($"析晶裕度      管内 {tb.DevitMarginMinK:+0;-0} K   " +
                                  $"(T_liq={p.TLiquidusC:0}, 要求 ≥{p.DevitMarginK:0})  " +
                                  $"{(tb.DevitMarginMinK >= p.DevitMarginK ? "✓" : "★风险")}");
                Console.WriteLine();
                Console.WriteLine($"铂用量        管 {c.MassTubeG:0} g + 法兰 {c.MassFlangePairG:0} g " +
                                  $"= {c.MassTotalG:0} g   (法兰占 {c.MassFlangePairG / c.MassTotalG * 100:0}%)");
                return;
            }

            // --cli --plate   法兰二维电流场
            if (args.Contains("--plate"))
            {
                var g = new FlangePlate();
                var (xt, wt) = g.Tangent();
                double aTube = Math.PI * (26.0 * 26.0 - 25.0 * 25.0);
                double I = 10.0 * aTube;                       // J_tube = 10 A/mm²
                Console.WriteLine($"切点 x={xt:0.00} 半宽={wt:0.00}   管截面={aTube:0.0} mm²   I={I:0} A");
                foreach (double h in new[] { 1.0, 0.5 })
                {
                    var f = PlateCurrent2D.Solve(g, I, Materials.PtResistivity(1300), h);
                    Console.WriteLine(
                        $"h={h:0.00}mm  网格 {f.Nx}×{f.Nz}  迭代 {f.Iterations}  残差 {f.Residual:E2}");
                    Console.WriteLine(
                        $"   电流 流入 {f.CurrentInA:0.0} A / 流出 {f.CurrentOutA:0.0} A  " +
                        $"守恒误差 {f.ConservationError:E2}");
                    Console.WriteLine(
                        $"   J_max = {f.JMaxAPerMm2:0.00} A/mm²   J_mean = {f.JMeanAPerMm2:0.00}   " +
                        $"J_tube = 10.00   比值 = {f.JMaxAPerMm2 / 10.0:0.00}");
                    Console.WriteLine($"   整片焦耳发热 = {f.TotalGenW:0.0} W");

                    var th = PlateThermal2D.Solve(g, f, p, tRootC: p.TSetC);
                    Console.WriteLine($"   [热] 迭代 {th.Iterations} 收敛 {th.Converged:E2}  " +
                                      $"能量残差 {th.EnergyResidual:E2}");
                    Console.WriteLine($"        T 最低 {th.TMinC:0.0} °C   最高 {th.TMaxC:0.0}");
                    Console.WriteLine($"        发热 {th.QGenW:0} W  散热 {th.QLossW:0} W  " +
                                      $"从管子抽热 {th.QFromTubeW:0} W   Φ = {th.PhiOverall:0.000}");
                    Console.WriteLine($"        [诊断] 逐格残差 Σ|R| = {th.CellResidualAbsW:0.000} W  " +
                                      $"ΣR = {th.CellResidualSignedW:0.000} W  孤立格点 = {th.IsolatedCells}");
                    Console.WriteLine($"        [诊断] 台账 gen+qtube-loss = " +
                                      $"{th.QGenW + th.QFromTubeW - th.QLossW:0.0} W");
                }
                return;
            }

            // --cli --opt   法兰联合扫描（保温厚度 × 外径 × 剖面）
            if (args.Contains("--opt"))
            {
                var rows = FlangeOptimizer.Scan(p,
                    insulMm: new[] { 0, 5, 10, 15, 20, 30.0 },
                    roMm: new[] { 25, 30, 35, 40, 50.0 },
                    shapes: new[] { FlangeShape.Rectangular, FlangeShape.IdealClipped });
                Console.WriteLine(FlangeOptimizer.Format(rows, p));
                return;
            }

            // --cli [case.json] --plots <目录>   导出场图 PNG
            int pi = Array.IndexOf(args, "--plots");
            if (pi >= 0 && pi + 1 < args.Length)
            {
                ApplicationConfiguration.Initialize();
                string dir = args[pi + 1];
                Directory.CreateDirectory(dir);
                double xView = Math.Min(p.TubeLengthMm * 0.5, Math.Max(30.0, 6.0 * r.DecayLengthMm));

                void Save(string name, Action<ScottPlot.WinForms.FormsPlot> draw)
                {
                    var fp = UI.FieldPlots.NewPlot();
                    draw(fp);
                    fp.Plot.SavePng(Path.Combine(dir, name), 1100, 620);
                    Console.WriteLine($"  → {name}");
                }

                Save("field_T.png", f => UI.FieldPlots.DrawField(f,
                    FieldMap.Build(p, r, FieldMap.Quantity.Temperature, xView), p, r, true));
                Save("field_J.png", f => UI.FieldPlots.DrawField(f,
                    FieldMap.Build(p, r, FieldMap.Quantity.CurrentDensity, xView), p, r, false));
                Save("field_qv.png", f => UI.FieldPlots.DrawField(f,
                    FieldMap.Build(p, r, FieldMap.Quantity.VolumetricHeat, xView), p, r, false));
                Save("profile_axial.png", f => UI.FieldPlots.DrawAxialProfile(f, r, p));
                Save("profile_flange.png", f => UI.FieldPlots.DrawFlangeProfile(f, r, p));
                Save("profile_thickness.png", f => UI.FieldPlots.DrawThicknessProfile(f, r, p));
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
