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
            Console.WriteLine($"ℓt={r.DecayLengthMm:0.0}mm  τ={r.TauMetalS:0}s  稳定裕度 {r.StabilityRatio:0.0}×");

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
        // 校核现状：法兰厚度就取 3dm 实测 2.0 mm

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
                        q.SizeWall = false;
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
                    q.SizeWall = false;
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
 q.BusbarClampTempC = clamp;
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
 q.BusbarClampTempC = clamp;
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
 qs.BusbarClampTempC = clamp;
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
 q.BusbarClampTempC = clamp;
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
 q.BusbarClampTempC = clamp;
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
 q.BusbarClampTempC = clamp;
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
                    q0.SizeWall = false;
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

            // --cli --ramp2 [法兰.3dm] [--i A]   两节点升温：法兰会不会在升温期跑到管子前面
            //
            // 回答「烧断在升温还是稳态」。--ramp 答不了 —— 它把法兰并进管子当同一个温度，
            // 于是「法兰比管热」在那个模型里结构性地不可能出现（见 RampTwoNode 的类注释）。
            //
            // 法兰的电阻由**壳电流场**给出（QGen_ref/I_ref²），秒级，不依赖稳态热解 ——
            // 这一点很关键：稳态解目前与现实冲突（§4.2q），不能拿它当升温核算的输入。
            if (args.Contains("--ramp2"))
            {
                int qi = Array.IndexOf(args, "--ramp2");
                string f3r = qi + 1 < args.Length && !args[qi + 1].StartsWith("--")
                             ? args[qi + 1] : Find3dm("Pt_Heater.3dm");
                int ii2 = Array.IndexOf(args, "--i");
                double iUser = ii2 >= 0 && ii2 + 1 < args.Length && double.TryParse(args[ii2 + 1], out var iv)
                               ? iv : double.NaN;
                // 现场实际升温速率（用户 2026-08-11：以温度控制功率，20 °C/h）
                int rti = Array.IndexOf(args, "--rate");
                double rateKPerH = rti >= 0 && rti + 1 < args.Length && double.TryParse(args[rti + 1], out var rv2)
                                   ? rv2 : 20.0;

                const double fromC = 25, targetC = 1150, hours = 3.0;
                double wall = p.WallMinMm;
                double areaTubeMm2 = Math.PI * wall * (p.TubeIdMm + wall);
                double iDesign = double.IsNaN(iUser) ? p.JAllowAPerMm2 * areaTubeMm2 : iUser;

                Console.WriteLine("=== 两节点升温：管 + 法兰各一个温度节点 ===");
                Console.WriteLine($"几何 {Path.GetFileName(f3r)}   空管 {fromC:0} → {targetC:0} °C / 限时 {hours:0.#} h");
                Console.WriteLine($"管壁 {wall:0.00} mm（截面 {areaTubeMm2:0.0} mm²）   " +
                                  $"设计电流 {iDesign:0} A" +
                                  (double.IsNaN(iUser) ? $"（= J_allow {p.JAllowAPerMm2:0.0} × 截面）" : "（命令行给定）"));
                Console.WriteLine();

                // ── 法兰：厚度场 → 壳网格 → 电流场 → 参考电阻。不解温度场。
                ShellMesh mesh;
                try
                {
                    var tfield = Geometry3dm.LoadThickness(f3r, "法兰", double.NaN, 1.0);
                    double holeR = p.TubeIdMm * 0.5 + wall;
                    mesh = FlangeMesher.BuildFromField(tfield, holeR, 0, 2.0, 11.0, 50.0);
                }
                catch (Exception ex) { Console.WriteLine("✗ 读几何失败：" + ex.Message); return; }

                double tRef = 1050;
                var scr = ShellCurrent.Solve(mesh, iDesign, Materials.PtResistivity(tRef) * 1e3, tRef);
                double qGenRef = 0;
                for (int k = 0; k < mesh.CellCount; k++)
                    qGenRef += Materials.PtResistivity(tRef) * 1e3 * scr.JMagAPerMm2[k] * scr.JMagAPerMm2[k]
                             * mesh.Thickness[k] * mesh.Area[k];
                double rFlangeRef = qGenRef / (iDesign * iDesign);

                double insulX = new FlangePlate().InsulBoundaryXResolved;
                double aIns = 0, aBare = 0;
                for (int k = 0; k < mesh.CellCount; k++)
                    if (mesh.Centroid[k].X >= insulX) aIns += mesh.Area[k]; else aBare += mesh.Area[k];

                double massG = mesh.VolumeMm3 * Materials.PtDensity * 1e-6;
                double holeRmm = p.TubeIdMm * 0.5 + wall;
                double rEq = Math.Sqrt(mesh.TotalArea / Math.PI + holeRmm * holeRmm);
                double tMeanFl = mesh.VolumeMm3 / Math.Max(1e-9, mesh.TotalArea);

                Console.WriteLine($"法兰单片：{massG:0} g   净面积 {mesh.TotalArea:0} mm²" +
                                  $"（保温 {aIns / mesh.TotalArea * 100:0} % / 裸露 {aBare / mesh.TotalArea * 100:0} %）" +
                                  $"   均厚 {tMeanFl:0.00} mm   等效外半径 {rEq:0.0} mm");
                Console.WriteLine($"          @{iDesign:0} A / {tRef:0} °C 时 自身发热 {qGenRef:0} W ⇒ " +
                                  $"单片电阻 {rFlangeRef * 1e6:0.0} μΩ   J_max {scr.JMaxAPerMm2:0.00} A/mm²");
                Console.WriteLine();

                // 温控模式要跑完整条 20 K/h 的斜坡（1125 K ÷ 20 ≈ 56 h），限时按速率给，
                // 另外三种是顶电流的快升温，仍用 3 h。
                double rampHoursTemp = (targetC - fromC) / Math.Max(0.1, rateKPerH) * 1.4;

                RampTwoNode.Inputs Make(double factor, RampControl mode) => new()
                {
                    WallMm = wall,
                    FlangeMassG = massG,
                    FlangeAreaInsulMm2 = aIns, FlangeAreaBareMm2 = aBare,
                    FlangeResistanceRefOhm = rFlangeRef, FlangeRefTempC = tRef,
                    HoleRadiusMm = holeRmm, PlateEqOuterRadiusMm = rEq, FlangeThickMm = tMeanFl,
                    DesignCurrentA = iDesign, FromC = fromC, TargetC = targetC,
                    MaxHours = mode == RampControl.TemperatureRamp ? rampHoursTemp : hours,
                    RampRateKPerH = rateKPerH, MaxCurrentA = iDesign,
                    SharedFactor = factor, Mode = mode
                };

                Console.WriteLine($"{"片",8}{"控制",8}{"电流 起→终 A",18}{"法兰峰值 °C",13}" +
                                  $"{"max(法兰−管) K",15}{"出现在 min",12}{"当时管温 °C",13}{"管到点 h",10}  判定");

                var kinds = new (string name, double factor)[]
                {
                    ("端片", 1.0),
                    ("共用片", Math.Sqrt(3.0))
                };
                var modes = new (string name, RampControl m)[]
                {
                    ("温控", RampControl.TemperatureRamp),   // 现场实际方式
                    ("恒流", RampControl.ConstantCurrent),
                    ("恒功率", RampControl.ConstantPower),
                    ("恒压", RampControl.ConstantVoltage)
                };

                RampTwoNodeResult? probe = null;
                RampTwoNodeResult? sharedTemp = null;
                foreach (var (kn, kf) in kinds)
                    foreach (var (mn, mm) in modes)
                    {
                        var rr = RampTwoNode.Solve(p, Make(kf, mm));
                        probe ??= rr;
                        if (kf > 1.5 && mm == RampControl.TemperatureRamp) sharedTemp = rr;
                        string v = rr.FlangeMelts ? "★ 法兰熔化"
                                 : rr.MaxFlangeMinusTubeK > 0 ? "✗ 法兰比管热"
                                 : rr.TubeReached ? "✓" : "✗ 升不到";
                        Console.WriteLine($"{kn,8}{mn,8}{$"{rr.CurrentStartA:0}→{rr.CurrentEndA:0}",18}" +
                            $"{rr.TFlangePeakC,13:0}{rr.MaxFlangeMinusTubeK,15:+0;-0}" +
                            $"{rr.TimeAtMaxDeltaS / 60,12:0.0}{rr.TTubeAtMaxDeltaC,13:0}" +
                            $"{(rr.TubeReached ? rr.HoursToTarget.ToString("0.00") : "—"),10}  {v}");
                        if (rr.Note.Length > 0) Console.WriteLine($"{"",16}  {rr.Note}");
                    }

                // ── 温控 + 共用片：沿斜坡逐点看「法兰比管热多少」，判断危险窗口在哪一段
                if (sharedTemp is not null && sharedTemp.Trace.Count > 2)
                {
                    Console.WriteLine();
                    Console.WriteLine($"温控 {rateKPerH:0.#} K/h、共用片 f=√3 沿程（法兰−管 的演化）：");
                    Console.WriteLine($"{"管温 °C",10}{"法兰 °C",11}{"差 K",9}{"电流 A",10}{"已用 h",9}");
                    double nextT = 100;
                    foreach (var (t, tt, tf, ia) in sharedTemp.Trace)
                    {
                        if (tt < nextT) continue;
                        Console.WriteLine($"{tt,10:0}{tf,11:0}{tf - tt,9:+0;-0}{ia,10:0}{t / 3600,9:0.0}");
                        nextT += 100;
                    }
                }

                // ── 叠加系数的临界值：升温期「法兰反超管子」的门槛
                //    机理是纯比值：法兰升温率 ∝ f²·I²R_f/C_f，管 ∝ I²R_t/C_t，
                //    冷态两边的散热都≈0 ⇒ 谁快谁跑前面，与电流绝对值无关。
                Console.WriteLine();
                Console.WriteLine($"共用片叠加系数 f 的临界值（**温控 {rateKPerH:0.#} K/h**，即现场实际方式）：");
                Console.WriteLine($"{"f",8}{"法兰峰值 °C",13}{"max(法兰−管) K",15}{"出现在 min",12}  判定");
                double fCrit = double.NaN, fPrev = double.NaN, dPrev = double.NaN;
                foreach (double f in new[] { 1.00, 1.10, 1.20, 1.25, 1.30, 1.40, 1.50, 1.732 })
                {
                    var rr = RampTwoNode.Solve(p, Make(f, RampControl.TemperatureRamp));
                    Console.WriteLine($"{f,8:0.000}{rr.TFlangePeakC,13:0}{rr.MaxFlangeMinusTubeK,15:+0;-0}" +
                                      $"{rr.TimeAtMaxDeltaS / 60,12:0.0}  " +
                                      (rr.MaxFlangeMinusTubeK > 0 ? "✗ 反超" : "✓"));
                    if (double.IsNaN(fCrit) && rr.MaxFlangeMinusTubeK > 0 && !double.IsNaN(fPrev))
                        fCrit = fPrev + (f - fPrev) * (0 - dPrev) / (rr.MaxFlangeMinusTubeK - dPrev);
                    fPrev = f; dPrev = rr.MaxFlangeMinusTubeK;
                }
                if (!double.IsNaN(fCrit))
                {
                    Console.WriteLine();
                    Console.WriteLine($"★ 临界叠加系数 ≈ **{fCrit:0.00}** —— 超过它，共用片在升温期就会比管子热。");
                    Console.WriteLine($"  §4.2h 推导的 √3 = 1.732 与工作簿的 1.5 **都在临界值之上**。");
                }

                if (probe is not null)
                {
                    Console.WriteLine();
                    Console.WriteLine($"热容：管 {probe.CapTubeJPerK:0} J/K（含保温）  法兰 {probe.CapFlangeJPerK:0} J/K   " +
                                      $"比 {probe.CapTubeJPerK / Math.Max(1e-9, probe.CapFlangeJPerK):0.0}×");
                    Console.WriteLine($"管↔法兰 耦合导度 {probe.CouplingWPerK:0.00} W/K   ⇒ 法兰时间常数 " +
                                      $"{probe.CapFlangeJPerK / Math.Max(1e-9, probe.CouplingWPerK) / 60:0.0} min");
                    Console.WriteLine("  —— 这个时间常数与升温全程同量级，就是「管子当不了法兰的散热器」的量化表述。");
                }
                Console.WriteLine();
                Console.WriteLine("恒压/恒功率的定值取「目标温度处跑出设计电流」，故冷启电流 = 设计电流 × R(热)/R(冷)。");
                Console.WriteLine("⚠ 真实可控矽是逐步开触发角的，恒压那行是**上界**（最坏情形），不是实际曲线；");
                Console.WriteLine("  要精确需要现场的升温电流曲线。恒流那行则是最温和的下界。");
                return;
            }

            // --cli --jlimit   「不烧断」的电流密度上限 —— 从能量平衡推出，不是经验值
            //
            // 一块通电的铂板，单位面积的发热是 ρe·J²·t，散热是两面各一份 q″(T)。
            // 稳态温度由二者相等定：
            //
            //     ρe(T)·J²·t = 2·q″(T)     ⇒     J_lim(T, t) = √( 2 q″(T) / (ρe(T)·t) )
            //
            // 同一个式子取不同的 T 就是不同的判据：
            //   T = 工作温度  → 「法兰不比管热」（用户的硬规则，§4.2k）
            //   T = 1768 °C   → 物理熔断上限
            //
            // ★ 注意 J_lim ∝ 1/√t 而实际 J = K/t ∝ 1/t（深度平均下面电流守恒），
            //   故加厚使 J_实际/J_lim ∝ 1/√t —— 加厚有用，但**收敛得很慢**。
            if (args.Contains("--jlimit"))
            {
                Console.WriteLine("=== 「不烧断」的电流密度上限（由能量平衡推出）===");
                Console.WriteLine("判据：ρe(T)·J²·t = 2·q″(T)  ⇒  J_lim = √(2q″/(ρe·t))");
                Console.WriteLine("  发热 ∝ 板厚（体积项），散热与板厚无关（表面项）⇒ 越厚，允许的 J 越低");
                Console.WriteLine();

                double charLen = 0.05;
                var insLayers = new List<InsulationLayer>
                {
                    new() { Name = "法兰保温", ThicknessMm = p.FlangeInsulThickMm,
                            K0 = p.Layer1.K0, K1 = p.Layer1.K1,
                            Enabled = p.FlangeInsulThickMm > 1e-6 }
                };

                // q″ 单面 W/m²；bare = 裸铂表面，ins = 包 p.FlangeInsulThickMm 的纤维
                double QBare(double tC) => Insulation.FlatOuterFlux(tC, p.TAmbC, p.PtEmissivity,
                                                charLen, p.LossScale, p.FlangeAirVelocityMPerS);
                double QIns(double tC) => Insulation.PlateFlux(tC, p.TAmbC, insLayers,
                                                p.OuterEmissivity, charLen, p.LossScale);

                double JLim(double tC, double tMm, bool bare)
                {
                    double q = bare ? QBare(tC) : QIns(tC);
                    double rho = Materials.PtResistivity(tC);          // Ω·m
                    return Math.Sqrt(2.0 * q / (rho * tMm * 1e-3)) * 1e-6;   // A/mm²
                }

                foreach (double tC in new[] { 1050.0, 1150.0, RampTwoNode.PtMeltingC })
                {
                    string what = tC >= RampTwoNode.PtMeltingC - 1
                        ? "熔断上限（板自身升到铂熔点）"
                        : $"「法兰不比管热」（管温 {tC:0} °C）";
                    Console.WriteLine($"── T = {tC:0} °C：{what}");
                    Console.WriteLine($"    单面散热 q″：裸露 {QBare(tC) / 1000:0.0} kW/m²   " +
                                      $"包 {p.FlangeInsulThickMm:0.0} mm 纤维 {QIns(tC) / 1000:0.0} kW/m²");
                    Console.WriteLine($"{"板厚 mm",9}{"J_lim 裸露",13}{"J_lim 保温",13}   [A/mm²]");
                    foreach (double t in new[] { 0.5, 1.0, 1.5, 2.0, 3.0, 4.0 })
                        Console.WriteLine($"{t,9:0.0}{JLim(tC, t, true),13:0.00}{JLim(tC, t, false),13:0.00}");
                    Console.WriteLine();
                }

                // ── 对照现役件
                double jWork = JLim(1150, 2.0, true);
                Console.WriteLine("── 对照现役件（2.0 mm，圆盘裸露口径）");
                Console.WriteLine($"    工作温度判据给出 J_lim = {jWork:0.00} A/mm²");
                Console.WriteLine($"    ★ 这正是代码里一直用的经验值 J_allow = {p.JAllowAPerMm2:0.0} ——");
                Console.WriteLine("      §4.2i 记的「J_allow=10 的物理依据待定」到此可以划掉：");
                Console.WriteLine("      它就是 2 mm 裸铂板在工作温度下的自热平衡点。");
                Console.WriteLine();
                Console.WriteLine($"    实测场：端片 J_max 14.53 / 共用片 23.97 A/mm²（--run 收敛解）");
                Console.WriteLine($"    ⇒ 端片超 {14.53 / jWork:0.00}×，共用片超 {23.97 / jWork:0.00}×，" +
                                  $"共用片同时超过熔断上限 {JLim(RampTwoNode.PtMeltingC, 2.0, true):0.00}");
                Console.WriteLine();

                // ── 加厚能不能救：J_实际 ∝ 1/t，J_lim ∝ 1/√t
                Console.WriteLine("── 只靠加厚共用片能不能压回来（J_实际 ∝ 1/t，J_lim ∝ 1/√t）");
                Console.WriteLine($"{"板厚 mm",9}{"J_实际",10}{"J_lim",10}{"利用率",10}{"单片 g",10}");
                double area2 = 23541.0;      // 现役片净面积 mm²（--geom 校核）
                foreach (double t in new[] { 2.0, 3.0, 4.0, 6.0, 8.0, 12.0 })
                {
                    double jAct = 23.97 * (2.0 / t);
                    double jL = JLim(1150, t, true);
                    Console.WriteLine($"{t,9:0.0}{jAct,10:0.00}{jL,10:0.00}{jAct / jL,10:0.00}" +
                                      $"{area2 * t * Materials.PtDensity * 1e-6,10:0}");
                }
                Console.WriteLine();
                Console.WriteLine("★ 加厚是**收敛很慢**的路：利用率只按 1/√t 下降，");
                Console.WriteLine("  把共用片压到 1.0 需要约 12 mm、单片约 6 kg —— 与省铂的目标正相反。");
                Console.WriteLine("  真正的杠杆是降电流（改接线相位）或扩散热面积，不是加厚。");
                Console.WriteLine();

                // ── 冷启的比值判据：J 判不了这一条
                Console.WriteLine("── 另一条 J 判不了的约束：冷启时「谁升得快」");
                Console.WriteLine("    冷态两边散热都≈0，于是比的是升温率：");
                Console.WriteLine("      dT_法兰/dt ÷ dT_管/dt = f²·(R_法兰/C_法兰) ÷ (R_管/C_管)");
                Console.WriteLine("    电流在里面**约掉了** ⇒ 这一条与 J 的绝对值无关，只与叠加系数 f 有关。");
                Console.WriteLine($"    现役几何：R_法兰 413 μΩ / C_法兰 144 J·K⁻¹，R_管 832 μΩ / C_管 189 J·K⁻¹");
                Console.WriteLine($"    ⇒ f ≤ √((832/189)×(144/413)) = {Math.Sqrt((832.0 / 189.0) * (144.0 / 413.0)):0.000}"
                                  + "（--ramp2 实扫得 1.20–1.25，吻合）");
                Console.WriteLine();
                Console.WriteLine("⚠ 上面的 J_lim 是**逐点**判据，没有计横向导热。铂板的翅片长度");
                Console.WriteLine("  √(k·t/(2·dq″/dT)) ≈ 24 mm（2 mm 板、1150 °C），与圆盘径向尺寸 34 mm 同量级 ——");
                Console.WriteLine("  ⇒ 小于 24 mm 的局部热点会被周围拉住，逐点判据对它**偏保守**；");
                Console.WriteLine("    严格判定仍要壳解。J_lim 的用途是定尺寸时的快速筛选。");
                return;
            }

            // --cli --solve4   ★ 四片法兰**各自独立**定厚：3 段约束 / 4 个自由度
            //
            // --sweep2 把四片绑成一个标度（t ∝ I），于是只能让**一段**落进 (0,10] K，
            // 其余段落在 +45…+104 K。而用户明确说过「四片法兰允许各自独立」——
            // 3 个段约束对 4 个厚度自由度，本来就是可解的（还余 1 维用来减重）。
            //
            // 解法：阻尼牛顿。段 i 的管根温差主要由它两端的片 i、i+1 决定，
            //   片 j 的误差取相邻段误差的均值，按灵敏度 d(ΔT)/d(ln t) ≈ 800 K 走步。
            if (args.Contains("--solve4"))
            {
                double wall4 = 0.4;
                double[] setp4 = { 1150, 1080, 1050 };
                const double target = 5.0, sens = 800.0;   // K per unit ln t（由 --tscan 斜率估）

                Console.WriteLine("=== 四片法兰各自独立定厚（3 段约束 / 4 自由度）===");
                Console.WriteLine($"管壁 {wall4:0.0} mm；靶：**每一段**管根温差 = +{target:0.#} K");
                Console.WriteLine("阻尼牛顿：片 j 的误差取相邻段误差均值，Δln t = −ω·err/灵敏度");
                Console.WriteLine();

                var cases4 = new (double tubeIns, double flIns, double bx, string nm)[]
                {
                    (2.5,  0.0,  1e9,  "纤维2.5/法兰全裸"),
                    (2.5, 20.0, -1e9,  "纤维2.5/法兰全包20"),
                    (10.0, 5.0, -1e9,  "纤维10/法兰全包5"),
                    (10.0,20.0, -1e9,  "纤维10/法兰全包20"),
                };
                var geo4 = new (double rd, double tabX, double hw, string nm)[]
                {
                    (30.0, -50.0, 20.0, "Ø60/舌50"),
                    (30.0, -80.0, 20.0, "Ø60/舌80"),
                };

                foreach (var (tubeIns, flIns, bx, cnm) in cases4)
                    foreach (var (rd, tabX, hw, gnm) in geo4)
                    {
                        var p4 = SegmentSolver.Clone(p);
                        p4.Layer1.ThicknessMm = tubeIns; p4.Layer1.Enabled = true;
                        p4.WallMinMm = wall4;
                        p4.FlangeInsulThickMm = flIns; p4.FlangeInsulated = flIns > 1e-6;
                        p4.BusbarClampTempC = 300;

                        var t4 = new[] { 0.6, 1.0, 1.0, 0.6 };     // 初值：共用片厚些
                        LineResult? last = null;
                        for (int it4 = 0; it4 < 22; it4++)
                        {
                            var plates = t4.Select(t => new FlangePlate
                            {
                                DiscRadiusMm = rd, HoleRadiusMm = 26.0,
                                TabEndXMm = tabX, TabEndHalfWidthMm = hw,
                                ThicknessMm = t, ThickenedMm = t, InsulBoundaryXMm = bx
                            }).ToArray();
                            var lc4 = new LineCase
                            {
                                Base = p4, WallMm = wall4, UseMeasuredCurrent = false,
                                SetpointC = setp4, CheckRamp = false, FlangePlates = plates
                            };
                            try { last = LineRunner.Run(lc4); } catch { last = null; break; }
                            if (last is null || !last.Ok) break;

                            var err = last.Segments.Select(s => s.RootDeltaK - target).ToArray();
                            double worstErr = err.Max(e => Math.Abs(e));
                            if (worstErr < 2.0) break;

                            // 片 j 的误差 = 相邻段误差均值（端片只有一个邻段）
                            for (int j4 = 0; j4 < t4.Length; j4++)
                            {
                                double e4 = j4 == 0 ? err[0]
                                          : j4 >= err.Length ? err[^1]
                                          : 0.5 * (err[j4 - 1] + err[j4]);
                                double step = -0.6 * e4 / sens;
                                step = Math.Clamp(step, -0.35, 0.35);
                                t4[j4] = Math.Clamp(t4[j4] * Math.Exp(step), 0.4, 6.0);
                            }
                        }

                        if (last is null || !last.Ok) { Console.WriteLine($"{cnm}/{gnm}  ✗ 求解失败"); continue; }
                        var dts = last.Segments.Select(s => s.RootDeltaK).ToArray();
                        double tmax4 = last.Flanges.Max(f => f.TMaxC);
                        bool ok4 = last.Converged && dts.All(d => d > 0 && d <= 10)
                                   && tmax4 <= RampTwoNode.PtMeltingC - 200;
                        Console.WriteLine($"── {cnm} / {gnm}");
                        Console.WriteLine($"   厚度 {string.Join(" / ", t4.Select(t => t.ToString("0.000")))} mm" +
                                          $"   段温差 {string.Join(" / ", dts.Select(d => d.ToString("+0.0;-0.0")))} K");
                        Console.WriteLine($"   法兰最高 {tmax4:0} °C   总铂 {last.TotalMassG:0} g" +
                                          $"（管 {last.TubeMassG:0} + 法兰 {last.FlangeMassG:0}）" +
                                          $"   省 {last.SavingPct:0.0} %   " +
                                          (last.Converged ? "" : "未收敛 ") + (ok4 ? "✓ 全过" : "✗"));
                        if (!ok4 && dts.Any(d => d > 10))
                            Console.WriteLine($"   卡在：最大段温差 {dts.Max():0.0} K > 10");
                        if (!ok4 && tmax4 > RampTwoNode.PtMeltingC - 200)
                            Console.WriteLine($"   卡在：法兰局部 {tmax4:0} °C，离熔点裕度不足 200 K");
                        Console.WriteLine();
                    }
                return;
            }

            // --cli --tscan   诊断：管根温差**真的**随法兰厚度单调吗？
            //
            // --sweep2 的二分靠「厚度↑ ⇒ 法兰发热↓ ⇒ 抽热↑ ⇒ 管根温差↑」这个单调性。
            // 但实跑后二分落点在 ±28…±255 K 而不是靶值 +5 K，相邻配置还正负乱跳
            // ⇒ 前提可疑。§7 记过同类坑（「Brent 求根遇噪声」）：**先画出函数再求根**。
            if (args.Contains("--tscan"))
            {
                double wallT = 0.4, tubeInsT = 2.5;
                var pt2 = SegmentSolver.Clone(p);
                pt2.Layer1.ThicknessMm = tubeInsT; pt2.Layer1.Enabled = true;
                pt2.WallMinMm = wallT;
                pt2.FlangeInsulThickMm = 0; pt2.FlangeInsulated = false;
                pt2.BusbarClampTempC = 300;

                Console.WriteLine("=== 诊断：管根温差 vs 法兰厚度（整线耦合解逐点）===");
                Console.WriteLine($"管壁 {wallT:0.0} / 管纤维 {tubeInsT:0.0} / 法兰全裸 / 夹持 300 °C");
                Console.WriteLine("形状 Ø60/舌50；共用片厚 = 端片厚 × √3");
                Console.WriteLine();
                Console.WriteLine($"{"端片t mm",10}{"HC1 ΔT",9}{"HC2 ΔT",9}{"HC3 ΔT",9}" +
                                  $"{"最差ΔT",9}{"法兰最高°C",12}{"Φ_max",8}{"总铂g",8}  收敛");

                foreach (double tE in new[] { 0.40, 0.55, 0.70, 0.85, 1.00, 1.30, 1.60, 2.00, 2.60, 3.20, 4.00 })
                {
                    FlangePlate MkT(double t) => new()
                    {
                        DiscRadiusMm = 30.0, HoleRadiusMm = 26.0,
                        TabEndXMm = -50.0, TabEndHalfWidthMm = 20.0,
                        ThicknessMm = t, ThickenedMm = t, InsulBoundaryXMm = 1e9
                    };
                    double kS = Math.Sqrt(3.0);
                    var lcT = new LineCase
                    {
                        Base = pt2, WallMm = wallT, UseMeasuredCurrent = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 }, CheckRamp = false,
                        FlangePlates = new[] { MkT(tE), MkT(tE * kS), MkT(tE * kS), MkT(tE) }
                    };
                    try
                    {
                        var rT = LineRunner.Run(lcT);
                        if (!rT.Ok) { Console.WriteLine($"{tE,10:0.00}   ✗ {rT.Message}"); continue; }
                        double worst = rT.Segments.OrderByDescending(s2 => Math.Abs(s2.RootDeltaK))
                                        .First().RootDeltaK;
                        Console.WriteLine($"{tE,10:0.00}{rT.Segments[0].RootDeltaK,9:+0.0;-0.0}" +
                            $"{rT.Segments[1].RootDeltaK,9:+0.0;-0.0}{rT.Segments[2].RootDeltaK,9:+0.0;-0.0}" +
                            $"{worst,9:+0.0;-0.0}{rT.Flanges.Max(f => f.TMaxC),12:0}" +
                            $"{rT.Flanges.Max(f => f.Phi),8:0.00}{rT.TotalMassG,8:0}  " +
                            (rT.Converged ? "✓" : "✗"));
                    }
                    catch (Exception ex) { Console.WriteLine($"{tE,10:0.00}   ✗ {ex.Message}"); }
                }
                Console.WriteLine();
                Console.WriteLine("若 ΔT 不随 t 单调，则 --sweep2 的二分无效，其「无可行解」不作数。");
                return;
            }

            // --cli --sweep2   把第 4 项自由度（法兰保温）扫完，厚度二分在**整线耦合解**里做
            //
            // 前几轮的教训都收在这里：
            //   · 逐片解把管根钉在 1150 °C，是乐观的 ⇒ 本命令一律用 LineRunner 出数
            //   · 法兰保温厚度此前只扫了 0 与 2.5 mm ⇒ 补到 0/2.5/5/10/20，含分区
            //   · 厚度按 t ∝ I 在端片与共用片间分配（§4.2b），只留一个标度做二分
            if (args.Contains("--sweep2"))
            {
                double wallS = 0.4;
                double[] setp = { 1150, 1080, 1050 };

                Console.WriteLine("=== 补扫法兰保温（第 4 自由度），厚度二分在整线耦合解内 ===");
                Console.WriteLine($"管壁固定 {wallS:0.0} mm（已证撞 0.4 下界）；靶：最不利段管根温差 = +5 K");
                Console.WriteLine("厚度按 t ∝ I 在端片/共用片间分配，只二分总标度");
                Console.WriteLine();

                var geoms = new (double rd, double tabX, double hw, string nm)[]
                {
                    (30.0, -50.0,  20.0, "Ø60/舌50"),
                    (30.0, -120.0, 20.0, "Ø60/舌120"),
                };
                var insOpts = new (double t, double bx, string nm)[]
                {
                    (0.0,   1e9,          "全裸"),
                    (5.0,  -1e9,          "全包5"),
                    (10.0, -1e9,          "全包10"),
                    (20.0, -1e9,          "全包20"),
                };

                Console.WriteLine($"{"管纤维",8}{"法兰保温",10}{"形状",12}{"端片t",8}{"共用t",8}" +
                                  $"{"minΔT",9}{"maxΔT",9}{"法兰最高°C",11}{"总铂 g",9}  判定");

                var found = new List<(double mass, string desc, double dT, double tmax)>();

                foreach (double tubeIns in new[] { 2.5, 10.0 })
                    foreach (var (rd, tabX, hw, gnm) in geoms)
                        foreach (var (fit, fbx, fnm) in insOpts)
                        {
                            var ps = SegmentSolver.Clone(p);
                            ps.Layer1.ThicknessMm = tubeIns; ps.Layer1.Enabled = true;
                            ps.WallMinMm = wallS;
                            ps.FlangeInsulThickMm = fit; ps.FlangeInsulated = fit > 1e-6;
                            ps.BusbarClampTempC = 300;      // 空冷（用户确认可行）

                            FlangePlate MkP(double t) => new()
                            {
                                DiscRadiusMm = rd, HoleRadiusMm = 26.0,
                                TabEndXMm = tabX, TabEndHalfWidthMm = hw,
                                ThicknessMm = t, ThickenedMm = t, InsulBoundaryXMm = fbx
                            };

                            // 端片走段电流、共用片走 √3 倍 ⇒ t ∝ I 分配
                            double kShared = Math.Sqrt(3.0);

                            // ★ 二分靶必须**连续单调**。此前用「|ΔT| 最大那段的带符号值」——
                            //   最不利段的身份一切换该量就跳变（HC2 的 −60 跳成 HC1 的 +55），
                            //   二分对不连续函数无效，于是 48 例全部误报「无解」。
                            //   各段 ΔT 各自随厚度单调 ⇒ 改用 min_i(ΔT_i) 做靶，再单独检查 max_i ≤ 10。
                            (double dTmin, double dTmax, double tmax, double mass, bool ok, bool conv)
                            Run(double tEnd)
                            {
                                var lc2 = new LineCase
                                {
                                    Base = ps, WallMm = wallS, UseMeasuredCurrent = false,
                                    SetpointC = setp, CheckRamp = false,
                                    FlangePlates = new[] { MkP(tEnd), MkP(tEnd * kShared),
                                                           MkP(tEnd * kShared), MkP(tEnd) }
                                };
                                try
                                {
                                    var r = LineRunner.Run(lc2);
                                    if (!r.Ok) return (0, 0, 0, 0, false, false);
                                    return (r.Segments.Min(s => s.RootDeltaK),
                                            r.Segments.Max(s => s.RootDeltaK),
                                            r.Flanges.Max(f => f.TMaxC), r.TotalMassG,
                                            true, r.Converged);
                                }
                                catch { return (0, 0, 0, 0, false, false); }
                            }

                            // 厚度↑ ⇒ 法兰发热↓ ⇒ 抽热↑ ⇒ 管根温差↑，单调，可二分到 +5 K
                            double lo2 = 0.4, hi2 = 4.0;
                            var rA = Run(lo2); var rB = Run(hi2);
                            if (!rA.ok || !rB.ok) continue;
                            if (rA.dTmin > 5.0 || rB.dTmin < 5.0)
                            {
                                Console.WriteLine($"{tubeIns,8:0.0}{fnm,10}{gnm,12}   ✗ 区间不跨 min ΔT=+5 K" +
                                                  $"（{rA.dTmin:+0;-0} … {rB.dTmin:+0;-0} K）");
                                continue;
                            }
                            for (int k2 = 0; k2 < 16; k2++)   // ΔT 斜率约 1300 K/mm ⇒ 要 ±0.008 mm 才落进 10 K 窗口
                            {
                                double mid = 0.5 * (lo2 + hi2);
                                var rm = Run(mid);
                                if (!rm.ok) break;
                                if (rm.dTmin < 5.0) lo2 = mid; else hi2 = mid;
                            }
                            double tE = 0.5 * (lo2 + hi2);
                            var rf2 = Run(tE);
                            if (!rf2.ok) continue;
                            bool okAll = rf2.conv && rf2.dTmin > 0 && rf2.dTmax <= 10
                                         && rf2.tmax <= RampTwoNode.PtMeltingC - 200;
                            string desc = $"纤维{tubeIns:0.0}/{fnm}/{gnm}/t端{tE:0.00}";
                            Console.WriteLine($"{tubeIns,8:0.0}{fnm,10}{gnm,12}{tE,8:0.00}" +
                                $"{tE * kShared,8:0.00}{rf2.dTmin,9:+0.0;-0.0}{rf2.dTmax,9:+0.0;-0.0}{rf2.tmax,11:0}" +
                                $"{rf2.mass,9:0}  " + (rf2.conv ? "" : "未收敛 ") +
                                (okAll ? "✓" : (rf2.dTmax > 10 || rf2.dTmin <= 0 ? "✗C2" : "") +
                                                (rf2.tmax > RampTwoNode.PtMeltingC - 200 ? "✗熔点裕度" : "")));
                            if (okAll) found.Add((rf2.mass, desc, rf2.dTmax, rf2.tmax));
                        }

                Console.WriteLine();
                found.Sort((a, b) => a.mass.CompareTo(b.mass));
                if (found.Count == 0)
                    Console.WriteLine("✗ 本轮无可行解。");
                else
                {
                    Console.WriteLine($"★ 可行解 {found.Count} 个，按总铂排序：");
                    foreach (var (m, d, dt, tm) in found.Take(8))
                        Console.WriteLine($"   {m,7:0} g   ΔT {dt:+0.0} K   法兰最高 {tm:0} °C   {d}");
                }
                return;
            }

            // --cli --final   对搜索出的最优配置跑**整线耦合解**做最终复核
            //
            // --plateopt 是逐片解：管根温度固定取 1150 °C。真实的管根温度由段↔法兰耦合定，
            // 且四片互相通过管子影响。最终数必须由 LineRunner 给。
            if (args.Contains("--final"))
            {
                // --solve4 的最优解（四片各自独立定厚）
                double wallF = 0.4, insF = 10.0, clampF = 300, flIns = 20.0;
                double rdF = 30.0, tabF = -50.0, hwF = 20.0;
                double[] tPlates = { 0.516, 0.855, 0.776, 0.426 };

                Console.WriteLine("=== 最优配置的整线耦合复核 ===");
                Console.WriteLine($"管壁 {wallF:0.00} mm / 管纤维 {insF:0.0} mm / 法兰全包 {flIns:0.0} mm / " +
                                  $"铜排夹持 {clampF:0} °C");
                Console.WriteLine($"四片 Ø{2 * rdF:0}/舌{-tabF:0}/半宽{hwF:0}，厚度各自独立 " +
                                  string.Join(" / ", tPlates.Select(t => t.ToString("0.000"))) + " mm");
                Console.WriteLine();

                var pf = SegmentSolver.Clone(p);
                pf.Layer1.ThicknessMm = insF; pf.Layer1.Enabled = true;
                pf.WallMinMm = wallF;
                pf.FlangeInsulThickMm = flIns; pf.FlangeInsulated = true;
                pf.BusbarClampTempC = clampF;

                FlangePlate Mk(double t) => new()
                {
                    DiscRadiusMm = rdF, HoleRadiusMm = 26.0,
                    TabEndXMm = tabF, TabEndHalfWidthMm = hwF,
                    ThicknessMm = t, ThickenedMm = t,
                    InsulBoundaryXMm = -1e9          // 全包
                };
                var platesF = tPlates.Select(Mk).ToArray();

                var lcF = new LineCase
                {
                    Base = pf,
                    WallMm = wallF,
                    UseMeasuredCurrent = false,
                    FlangePlates = platesF,
                    SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                    CheckRamp = true
                };

                var swF = System.Diagnostics.Stopwatch.StartNew();
                LineResult rF;
                try { rF = LineRunner.Run(lcF, new SyncProgress<string>(s => Console.WriteLine("  … " + s))); }
                catch (Exception ex) { Console.WriteLine("✗ " + ex.Message); return; }
                swF.Stop();
                if (!rF.Ok) { Console.WriteLine("✗ " + rF.Message); return; }

                Console.WriteLine();
                Console.WriteLine($"用时 {swF.Elapsed.TotalMinutes:0.0} min   " +
                                  $"收敛 {(rF.Converged ? "✓" : "✗ 结果不可用")}");
                Console.WriteLine();
                Console.WriteLine($"{"段",6}{"控温",7}{"电流 A",9}{"管 J",8}{"管根 °C",10}{"衔接温差 K",12}{"管重 g",9}");
                foreach (var s2 in rF.Segments)
                    Console.WriteLine($"{s2.Name,6}{s2.SetpointC,7:0}{s2.CurrentA,9:0}{s2.TubeJAPerMm2,8:0.00}" +
                        $"{s2.TRootC,10:0.0}{s2.RootDeltaK,12:+0.0;-0.0}{s2.MassG,9:0}");
                Console.WriteLine();
                Console.WriteLine($"{"法兰",10}{"电流 A",9}{"J_max",8}{"发热 W",9}{"表面散热",10}" +
                                  $"{"抽热 W",9}{"铜排带走 W",12}{"最高 °C",10}{"铂重 g",9}");
                foreach (var f2 in rF.Flanges)
                    Console.WriteLine($"{f2.Name,10}{f2.CurrentA,9:0}{f2.JMaxAPerMm2,8:0.00}" +
                        $"{f2.QGenW,9:0}{f2.QLossW,10:0}" +
                        $"{f2.QFromTubeW,9:+0;-0}{f2.QClampW,12:0}{f2.TMaxC,10:0.0}{f2.MassG,9:0}");
                Console.WriteLine($"  铜排夹持位置：舌片末端 x∈[{-50.0:0},{-47.0:0}] mm，" +
                                  $"全宽 {2 * hwF:0} mm，条带深 3 mm（模型按定温边界处理）");
                Console.WriteLine($"  ★ 四片合计需铜排带走 {rF.Flanges.Sum(f2 => f2.QClampW):0} W —— 铜排冷却按此选型");
                Console.WriteLine();
                foreach (var ck in rF.Checks)
                {
                    string mk = ck.Kind == CheckKind.HardSafety ? "★" : ck.Kind == CheckKind.Target ? "○" : "·";
                    string vd = ck.Kind == CheckKind.Reference ? "—" : ck.Undetermined ? "?" : ck.Ok ? "✓" : "✗";
                    string act = double.IsNaN(ck.Actual) ? "达不到" : ck.Actual.ToString("0.000");
                    Console.WriteLine($"  {mk}{ck.Name,-20}{act,12} / {ck.Limit,-10:0.000} {vd}  {ck.Where}");
                }
                Console.WriteLine();
                Console.WriteLine($"★ 整线总铂 {rF.TotalMassG:0} g（管 {rF.TubeMassG:0} + 法兰 {rF.FlangeMassG:0}）" +
                                  $"   基准 {rF.BaselineMassG:0} g   省 {rF.SavingPct:0.0} %");
                Console.WriteLine($"  玻璃温降 模型 {rF.GlassDropModelK:0.0} / 实测 {rF.GlassDropMeasuredK:0.0} K");
                foreach (var nt in rF.Notes) Console.WriteLine("  " + nt);
                return;
            }

            // --cli --grade   ★ 按 t ∝ 1/r² 做多级阶梯：把 Ψ 压向 1
            //
            // §4.2y 的 Ψ 表明：等厚板（含对称进电）Ψ ∈ [2.0, 10.5]，永远 >1 ⇒ C1 无解。
            // 而局部单位面积发热 = ρe·K²/t，径向流 K ∝ 1/r ⇒ **令 t ∝ 1/r² 则处处相等，Ψ → 1**。
            // 这正是用户给的「厚度可阶梯式分布」。本命令用 N 级台阶逼近该廓形并实测 Ψ。
            if (args.Contains("--grade"))
            {
                Console.WriteLine("=== t ∝ 1/r² 多级阶梯：把 Ψ 压向 1 ===");
                Console.WriteLine("局部单位面积发热 = ρe·K²/t，径向流 K ∝ 1/r ⇒ t ∝ 1/r² 使其处处相等");
                Console.WriteLine();
                Console.WriteLine($"{"形状",20}{"级数",6}{"厚度比",9}{"ΣR",9}{"ΣJ",10}{"Ψ",9}" +
                                  $"{"vs 等厚",9}  判定");

                foreach (var (rd, tabX, halfW) in new[]
                {
                    (60.0, -200.0, 20.0), (60.0, -120.0, 20.0),
                    (44.0, -120.0, 20.0), (30.0, -120.0, 20.0)
                })
                {
                    double psiFlat = double.NaN;
                    foreach (int nStep in new[] { 1, 3, 6, 10 })
                    {
                        var radii = new double[nStep];
                        var thick = new double[nStep];
                        double r0g = 26.0;
                        for (int k = 0; k < nStep; k++)
                        {
                            radii[k] = r0g + (rd - r0g) * (k + 1) / nStep;
                            double rMid = r0g + (rd - r0g) * (k + 0.5) / nStep;
                            // t ∝ 1/r²，以外缘厚 1.0 为基准
                            thick[k] = Math.Pow(rd / rMid, 2.0);
                        }
                        var g = new FlangePlate
                        {
                            DiscRadiusMm = rd, HoleRadiusMm = 26.0,
                            TabEndXMm = tabX, TabEndHalfWidthMm = halfW,
                            ThicknessMm = 1.0, ThickenedMm = 1.0, InsulBoundaryXMm = 1e9,
                            DiscStepRadiiMm = nStep > 1 ? radii : Array.Empty<double>(),
                            DiscStepThicknessMm = nStep > 1 ? thick : Array.Empty<double>()
                        };
                        try
                        {
                            var m = FlangeMesher.Build(g, 0, 1.0, 6.0, 70.0);
                            if (m.CellCount < 50) continue;
                            var s = DesignScreen.Extract(m, 1000.0, 1050.0, g.Tangent().X);
                            double psi = s.AreaMm2 * s.ShapeJ * s.ShapeJ / s.ShapeR;
                            if (nStep == 1) psiFlat = psi;
                            double ratio = nStep > 1 ? thick[0] : 1.0;
                            Console.WriteLine($"{$"Ø{2 * rd:0}/舌{-tabX:0}/半宽{halfW:0}",20}" +
                                $"{(nStep == 1 ? "等厚" : nStep.ToString()),6}{ratio,9:0.00}" +
                                $"{s.ShapeR,9:0.000}{s.ShapeJ,10:0.0000}{psi,9:0.00}" +
                                $"{(double.IsNaN(psiFlat) ? 1 : psi / psiFlat),9:0.00}" +
                                $"  {(psi <= 1.0 ? "✓ Ψ≤1" : psi < 1.5 ? "≈" : "✗")}");
                        }
                        catch { }
                    }
                    Console.WriteLine();
                }
                Console.WriteLine("厚度比 = 孔周环厚 ÷ 外缘厚。级数越多越逼近连续廓形。");
                Console.WriteLine("Ψ ≤ 1 意味着热自给的板其局部峰值也不超过设计温度 ⇒ C1 与 C2 可同时满足。");
                return;
            }

            // --cli --psi   ★ 形状数 Ψ：把「为什么无解」化成一个与电流/厚度/保温全无关的纯几何量
            //
            // 热自给（C2）要求 整片发热 = 整片散热，即 ρe·J_rms²·t = 2q″。
            // 而局部峰值处的单位面积发热是 ρe·J_max²·t。两式相除：
            //
            //     峰值发热 / 散热 = (J_max/J_rms)² ≡ **Ψ**
            //
            // 代入形状因子（J_max = ΣJ·I/t，整片发热 = I²ρe·ΣR/t）：
            //
            //     Ψ = A·ΣJ² / ΣR      ← **电流 I、厚度 t、保温 q″ 全部约掉了**
            //
            // ⇒ Ψ 是**纯形状数**。只要 Ψ>1，热自给的板其峰值处就必然发热大于散热，
            //   靠横向导热往外泄，局部温度必然高于设计温度。**这就是 674 例全挂的根**。
            if (args.Contains("--psi"))
            {
                Console.WriteLine("=== 形状数 Ψ = A·ΣJ²/ΣR = (J_max/J_rms)² ===");
                Console.WriteLine("热自给(C2) ⇒ J_rms = J_lim ⇒ 峰值处单位面积发热是散热的 Ψ 倍");
                Console.WriteLine("**电流、厚度、保温全部约掉** —— Ψ 只由形状决定，是 C1 能否满足的第一性判据");
                Console.WriteLine();
                Console.WriteLine($"{"形状",22}{"净面积 mm²",12}{"ΣR",9}{"ΣJ",10}{"Ψ",9}" +
                                  $"{"峰值温比",10}  判定");

                double tRef2 = 1150 + 273.15;
                foreach (double rd in new[] { 30.0, 34.0, 44.0, 60.0 })
                    foreach (double tabX in new[] { -50.0, -120.0, -200.0 })
                        foreach (double halfW in new[] { 20.0, 40.0 })
                        {
                            var g = new FlangePlate
                            {
                                DiscRadiusMm = rd, HoleRadiusMm = 26.0,
                                TabEndXMm = tabX, TabEndHalfWidthMm = halfW,
                                ThicknessMm = 1.0, ThickenedMm = 1.0, InsulBoundaryXMm = 1e9
                            };
                            if (rd >= Math.Sqrt(tabX * tabX + halfW * halfW)) continue;
                            try
                            {
                                var m = FlangeMesher.Build(g, 0, 1.5, 9.0, 50.0);
                                if (m.CellCount < 50) continue;
                                var s = DesignScreen.Extract(m, 1000.0, 1050.0, g.Tangent().X);
                                double psi = s.AreaMm2 * s.ShapeJ * s.ShapeJ / s.ShapeR;
                                // 局部平衡温度：q ∝ T⁴（辐射主导）⇒ 温比 = Ψ^(1/4)（绝对温标）
                                double tPeak = tRef2 * Math.Pow(psi, 0.25) - 273.15;
                                Console.WriteLine($"{$"Ø{2 * rd:0}/舌{-tabX:0}/半宽{halfW:0}",22}" +
                                    $"{s.AreaMm2,12:0}{s.ShapeR,9:0.000}{s.ShapeJ,10:0.0000}" +
                                    $"{psi,9:0.00}{tPeak,10:0}  {(psi <= 1.0 ? "✓" : "✗ Ψ>1")}");
                            }
                            catch { }
                        }

                Console.WriteLine();
                Console.WriteLine("「峰值温比」= 辐射主导下局部平衡温度 = T_工作·Ψ^(1/4)（绝对温标），");
                Console.WriteLine("  未计横向导热，故是**上界**；壳解实测比它低（导热把热点摊开）。");
                Console.WriteLine();
                Console.WriteLine("── 理论下界：即使**完全轴对称**进电，Ψ 也不会到 1");
                Console.WriteLine("  环形板径向流：K ∝ 1/r ⇒ J ∝ 1/(r·t)。等厚时");
                double r0 = 26, rr = 60;
                double jrms2 = Math.Log(rr / r0) / ((rr * rr - r0 * r0) / 2);
                double jmax2 = 1.0 / (r0 * r0);
                Console.WriteLine($"    Ψ_轴对称 = (1/r0²)/(ln(R/r0)/((R²−r0²)/2)) = {jmax2 / jrms2:0.00}" +
                                  $"（r0=26, R=60）");
                Console.WriteLine("  ⇒ **对称进电只能把 Ψ 从 3–6 降到约 2.6，仍然 >1。**");
                Console.WriteLine();
                Console.WriteLine("── 唯一能把 Ψ 压到 1 的办法：**厚度按 t ∝ 1/r² 渐变**");
                Console.WriteLine("  局部单位面积发热 = ρe·K²/t，而径向流 K ∝ 1/r ⇒ 令 t ∝ 1/r² 则处处相等。");
                Console.WriteLine($"  r 从 26 到 60 ⇒ 厚度比 (60/26)² = {Math.Pow(rr / r0, 2):0.0}× —— 孔周最厚、外缘最薄。");
                Console.WriteLine("  这正是用户给的「厚度可阶梯式分布」，用多级台阶逼近即可（--grade）。");
                return;
            }

            // --cli --stepopt [--f 系数]   ★ 阶梯厚度：用户给的 X1-A，直接对着 C1 的病根
            //
            // 等厚板在 648 个配置里全部挂 C1（孔周局部过热），而机理是明确的：
            //   单位面积发热 = ρe·J²·t = ρe·(K/t)²·t = **ρe·K²/t**   （K = J·t 是面电流，守恒）
            // ⇒ **加厚一处就按倍数降低该处的单位面积发热**。孔周正是 J 峰值所在。
            // 于是把圆盘做成阶梯：孔周一圈厚 k·t，其余（含舌片）厚 t，对 t 二分求抽热达标。
            if (args.Contains("--stepopt"))
            {
                int fi5 = Array.IndexOf(args, "--f");
                double fS = fi5 >= 0 && fi5 + 1 < args.Length && double.TryParse(args[fi5 + 1], out var fv5)
                            ? fv5 : Math.Sqrt(3.0);
                const double tLo = 0.4, tHi = 8.0;

                Console.WriteLine("=== 阶梯厚度法兰（孔周加厚）===");
                Console.WriteLine("机理：单位面积发热 = ρe·K²/t（K=J·t 面电流守恒）⇒ 加厚处发热按倍数下降");
                Console.WriteLine($"共用片叠加系数 f = {fS:0.000}；孔周环厚 = k × 外缘厚，对外缘厚二分求抽热达标");
                Console.WriteLine();

                (double draw, double tmax, double phi, double jmax, double mass, bool ok)
                Probe2(double rd, double tabX, double halfW, double tOut, double rStep, double k,
                       double iPlate, double tRootC, DesignInputs q)
                {
                    try
                    {
                        var gg = new FlangePlate
                        {
                            DiscRadiusMm = rd, HoleRadiusMm = 26.0,
                            TabEndXMm = tabX, TabEndHalfWidthMm = halfW,
                            ThicknessMm = tOut, ThickenedMm = tOut,
                            InsulBoundaryXMm = 1e9,
                            DiscStepRadiiMm = k > 1.0001 ? new[] { rStep } : Array.Empty<double>(),
                            DiscStepThicknessMm = k > 1.0001 ? new[] { tOut * k } : Array.Empty<double>()
                        };
                        var m = FlangeMesher.Build(gg, 0, 1.5, 9.0, 50.0);
                        if (m.CellCount < 50) return (0, 0, 0, 0, 0, false);
                        var sc3 = ShellCurrent.Solve(m, iPlate,
                                      Materials.PtResistivity(tRootC) * 1e3, tRootC);
                        var q3 = SegmentSolver.Clone(q); q3.TSetC = tRootC;
                        var th3 = ShellThermal.Solve(m, sc3.JMagAPerMm2, q3, tRootC, 1e9);
                        return (th3.QFromTubeW, th3.TMaxC, th3.PhiOverall, sc3.JMaxAPerMm2,
                                m.VolumeMm3 * Materials.PtDensity * 1e-6, th3.Converged);
                    }
                    catch { return (0, 0, 0, 0, 0, false); }
                }

                foreach (var (wall, ins) in new[] { (0.4, 2.5), (0.8, 2.5), (0.4, 10.0) })
                {
                    var q = SegmentSolver.Clone(p);
                    q.Layer1.ThicknessMm = ins; q.Layer1.Enabled = true;
                    q.WallMinMm = wall;
                    q.FlangeInsulThickMm = 0; q.FlangeInsulated = false;
                    q.BusbarClampTempC = 80;          // 空冷（用户确认可行），对 C1 最有利

                    double budget = DesignScreen.DrawBudgetW(q, wall, 1150, 10.0);
                    double ri = q.TubeIdMm * 0.5e-3, w3 = wall * 1e-3, rO = ri + w3;
                    double aM2 = Math.PI * (rO * rO - ri * ri);
                    double lossW = Insulation.CylinderLoss(1150, q.TAmbC, rO, q.Layers,
                                       q.OuterEmissivity, false, q.TubeLength, q.LossScale).QPerLength
                                   * q.TubeLength;
                    double iSeg = Math.Sqrt(lossW / (Materials.PtResistivity(1150) * q.TubeLength / aM2));

                    Console.WriteLine($"── 管壁 {wall:0.0} / 纤维 {ins:0.0} / 法兰全裸 / 铜排空冷 80 °C");
                    Console.WriteLine($"   段电流 {iSeg:0} A   C2 预算 {budget:0.0} W");
                    Console.WriteLine($"   {"形状",14}{"片",6}{"阶梯",14}{"外缘厚",9}{"抽热 W",9}" +
                                      $"{"最高 °C",10}{"Φ",8}{"J_max",8}{"铂重 g",9}  判定");

                    foreach (var (rd, tabX, halfW) in new[]
                    {
                        (44.0, -50.0, 40.0), (44.0, -80.0, 40.0),
                        (60.0, -50.0, 40.0), (60.0, -120.0, 40.0)
                    })
                        foreach (var (kind, iPlate) in new[] { ("端片", iSeg), ("共用", fS * iSeg) })
                            foreach (double rStep in new[] { 32.0, 38.0 })
                                foreach (double k in new[] { 1.0, 2.0, 3.0, 4.0 })
                                {
                                    if (k <= 1.0001 && rStep > 33) continue;   // 无阶梯只跑一次
                                    if (rStep >= rd - 1) continue;
                                    double lo = tLo, hi = tHi;
                                    var a1 = Probe2(rd, tabX, halfW, lo, rStep, k, iPlate, 1150, q);
                                    var b1 = Probe2(rd, tabX, halfW, hi, rStep, k, iPlate, 1150, q);
                                    if (!a1.ok || !b1.ok) continue;
                                    double target = budget * 0.5;
                                    if (a1.draw > target || b1.draw < target) continue;
                                    for (int it3 = 0; it3 < 10; it3++)
                                    {
                                        double mid = 0.5 * (lo + hi);
                                        var fm = Probe2(rd, tabX, halfW, mid, rStep, k, iPlate, 1150, q);
                                        if (!fm.ok) break;
                                        if (fm.draw < target) lo = mid; else hi = mid;
                                    }
                                    double tS = 0.5 * (lo + hi);
                                    var r4 = Probe2(rd, tabX, halfW, tS, rStep, k, iPlate, 1150, q);
                                    bool okC2 = r4.draw > 0 && r4.draw <= budget;
                                    bool okC1 = r4.tmax <= 1150 + 1e-6;
                                    if (!okC1 && r4.tmax > 1600) continue;      // 差太远的不打印
                                    string stepDesc = k <= 1.0001 ? "等厚"
                                                    : $"r≤{rStep:0}厚{k:0}×";
                                    Console.WriteLine($"   {$"Ø{2 * rd:0}/舌{-tabX:0}",14}{kind,6}{stepDesc,14}" +
                                        $"{tS,9:0.000}{r4.draw,9:+0.0;-0.0}{r4.tmax,10:0}{r4.phi,8:0.000}" +
                                        $"{r4.jmax,8:0.00}{r4.mass,9:0}  " +
                                        (okC2 ? "✓C2" : "✗C2") + (okC1 ? " ✓C1" : " ✗C1"));
                                }
                    Console.WriteLine();
                }
                Console.WriteLine("只打印局部峰值 ≤1600 °C 的行（差太远的省略）。两条同时 ✓ 才是可行片。");
                return;
            }

            // --cli --plateopt [--f 系数]   逐片形状优化：**厚度二分放进壳解里**
            //
            // 阶段 A 的闭式对法兰是**不够的**（2026-08-11 实测）：它假设整片均温 = 管根温度，
            // 而真实温度场极不均匀（单侧舌片进电 ⇒ 孔周电流集中，J_max/J_mean = 2.17），
            // 于是闭式选出的厚度在完整场解下给出 1000–3400 °C 的局部峰值与 38–197 K 的管根温差。
            //
            // 本命令对每个 (形状, 电流) 用**壳解本身**二分厚度，判据全部取自场解：
            //   · 抽热 QFromTubeW（能量恒等式给出，§7）落在 C2 预算内且为正
            //   · 局部最高温 TMaxC ≤ 工作温度（C1，真正管住局部的那一条）
            if (args.Contains("--plateopt"))
            {
                int fi4 = Array.IndexOf(args, "--f");
                double fSh = fi4 >= 0 && fi4 + 1 < args.Length && double.TryParse(args[fi4 + 1], out var fv4)
                             ? fv4 : Math.Sqrt(3.0);
                const double tMin = 0.4, tMax = 8.0;

                Console.WriteLine("=== 逐片形状优化（厚度二分在壳解内做，判据全取自场解）===");
                Console.WriteLine($"共用片叠加系数 f = {fSh:0.000}；厚度区间 [{tMin:0.0}, {tMax:0.0}] mm");
                Console.WriteLine();

                // 评估一片：给定形状与厚度，跑 FV 电流场 + FV 温度场
                (double draw, double tmax, double phi, double jmax, double mass, bool ok)
                Probe(FlangePlate g, double t, double iPlate, double tRootC, DesignInputs q)
                {
                    try
                    {
                        var gg = new FlangePlate
                        {
                            DiscRadiusMm = g.DiscRadiusMm, HoleRadiusMm = g.HoleRadiusMm,
                            TabEndXMm = g.TabEndXMm, TabEndHalfWidthMm = g.TabEndHalfWidthMm,
                            ThicknessMm = t, ThickenedMm = t,
                            InsulBoundaryXMm = g.InsulBoundaryXMm
                        };
                        var m = FlangeMesher.Build(gg, 0, 2.0, 11.0, 45.0);
                        if (m.CellCount < 50) return (0, 0, 0, 0, 0, false);
                        var sc2 = ShellCurrent.Solve(m, iPlate,
                                      Materials.PtResistivity(tRootC) * 1e3, tRootC);
                        var q2 = SegmentSolver.Clone(q); q2.TSetC = tRootC;
                        var th2 = ShellThermal.Solve(m, sc2.JMagAPerMm2, q2, tRootC,
                                                     gg.InsulBoundaryXResolved);
                        return (th2.QFromTubeW, th2.TMaxC, th2.PhiOverall, sc2.JMaxAPerMm2,
                                m.VolumeMm3 * Materials.PtDensity * 1e-6, th2.Converged);
                    }
                    catch { return (0, 0, 0, 0, 0, false); }
                }

                // ★ 法兰保温与铜排夹持是用户明确给出的两个自由度（「法兰可完全不包」「可以空冷」），
                //   而它们直接控制正在失败的那两条约束：
                //     法兰保温 ↑ ⇒ 自身散热 ↓ ⇒ 同一厚度下 Φ ↑（利 C2），但局部更热（不利 C1）
                //     铜排夹冷 ⇒ 从舌片端抽热 ⇒ 局部峰值 ↓（利 C1），但整片更凉、抽管子的热更多（不利 C2）
                //   两者方向相反，必须一起扫，否则等于自己砍掉了解空间。
                var flangeIns = new (string name, double thickMm, double boundaryX)[]
                {
                    ("全裸",       0.0, 1e9),
                    ("仅盘包2.5", 2.5, double.NaN),   // NaN ⇒ 取切点：圆盘包、舌片裸（现场实况）
                    ("全包2.5",   2.5, -1e9)
                };
                var clamps = new[] { -1.0, 300.0, 80.0 };   // -1 = 无夹冷（自由辐射端）

                foreach (double wall in new[] { 0.4, 0.8 })
                    foreach (double ins in new[] { 2.5, 20.0 })
                    foreach (var (finsName, finsT, finsX) in flangeIns)
                    foreach (double clampC in clamps)
                    {
                        var q = SegmentSolver.Clone(p);
                        q.Layer1.ThicknessMm = ins; q.Layer1.Enabled = true;
                        q.WallMinMm = wall;
                        q.FlangeInsulThickMm = finsT; q.FlangeInsulated = finsT > 1e-6;
                        q.BusbarClampTempC = clampC;

                        double budget = DesignScreen.DrawBudgetW(q, wall, 1150, 10.0);

                        // 段电流（空管稳态口径）
                        double ri = q.TubeIdMm * 0.5e-3, w2 = wall * 1e-3, rO = ri + w2;
                        double aM2 = Math.PI * (rO * rO - ri * ri);
                        double lossW = Insulation.CylinderLoss(1150, q.TAmbC, rO, q.Layers,
                                           q.OuterEmissivity, false, q.TubeLength, q.LossScale).QPerLength
                                       * q.TubeLength;
                        double iSeg = Math.Sqrt(lossW / (Materials.PtResistivity(1150) * q.TubeLength / aM2));

                        string cfg = $"壁{wall:0.0}/纤维{ins:0.0}/{finsName}/夹" +
                                     (clampC < 0 ? "无" : $"{clampC:0}");
                        int nFeas = 0, nFeasRelaxed = 0, nFailC1 = 0, nFailC2 = 0, nNoBracket = 0;
                        var lines = new List<string>();

                        foreach (double rd in new[] { 34.0, 44.0, 60.0 })
                            foreach (double tabX in new[] { -50.0, -120.0, -200.0 })
                            {
                                var g = new FlangePlate
                                {
                                    DiscRadiusMm = rd, HoleRadiusMm = 26.0,
                                    TabEndXMm = tabX, TabEndHalfWidthMm = Math.Min(40.0, rd - 4),
                                    InsulBoundaryXMm = finsX
                                };
                                if (rd >= Math.Sqrt(tabX * tabX + g.TabEndHalfWidthMm * g.TabEndHalfWidthMm))
                                    continue;

                                foreach (var (kind, iPlate, tRoot) in new[]
                                {
                                    ("端片", iSeg, 1150.0),
                                    ("共用", fSh * iSeg, 1150.0)
                                })
                                {
                                    // 抽热随厚度单调增（越厚发热越少）⇒ 二分求 draw = budget/2
                                    double lo = tMin, hi = tMax;
                                    var fLo = Probe(g, lo, iPlate, tRoot, q);
                                    var fHi = Probe(g, hi, iPlate, tRoot, q);
                                    if (!fLo.ok || !fHi.ok) continue;
                                    double target = budget * 0.5;
                                    if (fLo.draw > target || fHi.draw < target) { nNoBracket++; continue; }
                                    for (int it2 = 0; it2 < 10; it2++)
                                    {
                                        double mid = 0.5 * (lo + hi);
                                        var fm = Probe(g, mid, iPlate, tRoot, q);
                                        if (!fm.ok) break;
                                        if (fm.draw < target) lo = mid; else hi = mid;
                                    }
                                    double tSol = 0.5 * (lo + hi);
                                    var r3 = Probe(g, tSol, iPlate, tRoot, q);
                                    bool okC2 = r3.draw > 0 && r3.draw <= budget;
                                    // C1 有两种读法，差别很大，一并给出：
                                    //   严：局部峰值 ≤ 管温（本条使 674 例全挂）
                                    //   宽：净热流方向安全（draw>0，即不倒灌，用户描述的「功率往法兰堆」没发生）
                                    //       且局部峰值离铂熔点 1768 °C 有 200 K 裕度
                                    bool okC1 = r3.tmax <= tRoot + 1e-6;
                                    bool okC1Relaxed = r3.draw > 0 && r3.tmax <= RampTwoNode.PtMeltingC - 200;
                                    if (okC1 && okC2) nFeas++;
                                    else if (!okC1) nFailC1++;
                                    else nFailC2++;
                                    if (okC1Relaxed && okC2) nFeasRelaxed++;
                                    // 留下：严判可行的、宽判可行的、以及接近的
                                    if ((okC1 && okC2) || (okC1Relaxed && okC2) || r3.tmax - tRoot < 200)
                                        lines.Add($"{$"Ø{2 * rd:0}/舌{-tabX:0}",16}{kind,7}{tSol,8:0.000}" +
                                            $"{r3.draw,9:+0.0;-0.0}{r3.tmax,10:0}{r3.phi,8:0.000}" +
                                            $"{r3.jmax,8:0.00}{r3.mass,9:0}  " +
                                            (okC2 ? "✓C2" : "✗C2") + (okC1 ? " ✓C1严" : " ✗C1严")
                                            + (okC1Relaxed ? " ✓C1宽" : " ✗C1宽"));
                                }
                            }

                        Console.WriteLine($"── {cfg,-30} 段电流 {iSeg:0} A  C2预算 {budget:0.0} W  " +
                                          $"→ 严判可行 {nFeas} / **宽判可行 {nFeasRelaxed}** / C1挂 {nFailC1} / C2挂 {nFailC2} / 无区间 {nNoBracket}");
                        if (lines.Count > 0)
                        {
                            Console.WriteLine($"   {"形状",16}{"片",7}{"厚 mm",8}{"抽热 W",9}{"最高 °C",10}" +
                                              $"{"Φ",8}{"J_max",8}{"铂重 g",9}");
                            foreach (var ln in lines) Console.WriteLine("   " + ln);
                        }
                    }
                Console.WriteLine("读法：两条同时 ✓ 才是可行片。C1 卡住说明孔周局部过热 ——");
                Console.WriteLine("  单侧舌片进电使电流在孔周一侧集中（J_max/J_mean ≈ 2.17），");
                Console.WriteLine("  这是**形状**问题，不是厚度问题：加厚会同时把抽热推出 C2 预算。");
                return;
            }

            // --cli --optimize [--f 系数] [--verify N]   按总纲求最小铂重
            //
            // 两阶段。**报告值一律取第二阶段**（完整 FV 场解 + 段↔法兰耦合解），
            // 第一阶段只用来把候选从上万个压到几个 —— 它是搜索加速器，不是结论来源。
            //
            //   阶段 A（解析筛选，微秒/点）：每个**形状**跑一次壳电流场取形状因子
            //     （R = ρe·ΣR/t，J_max = ΣJ·I/t），之后厚度与电流的扫描全是闭式；
            //     用 §4.2v 的自给质量式与三条闭式判据排除不可行点。
            //   阶段 B（第一性，分钟/点）：对存活的前 N 名，用 LineRunner 跑整线耦合解 ——
            //     变步长壳网格 → 有限体积电流场 → 有限体积温度场 → 段↔法兰欠松弛耦合到收敛，
            //     取真实的管根温差、逐片局部最高温、玻璃温降与铂重。
            if (args.Contains("--optimize"))
            {
                int fi3 = Array.IndexOf(args, "--f");
                double fShared = fi3 >= 0 && fi3 + 1 < args.Length && double.TryParse(args[fi3 + 1], out var fv3)
                                 ? fv3 : Math.Sqrt(3.0);
                int vi = Array.IndexOf(args, "--verify");
                int nVerify = vi >= 0 && vi + 1 < args.Length && int.TryParse(args[vi + 1], out var nv) ? nv : 3;

                double[] setpoints = { 1150, 1080, 1050 };
                const double tMinMm = 0.4;           // 用户给的「太薄没意义」下界
                int nSeg = setpoints.Length;

                Console.WriteLine("=== 按总纲求最小铂重（HANDOVER §0.0）===");
                Console.WriteLine($"目标 min 整线总铂重；C1 升温可达且不烧；C2 稳态管根温差 0<ΔT<10 K");
                Console.WriteLine($"厚度下界 {tMinMm:0.0} mm（用户给定）；共用片叠加系数 f = {fShared:0.000}");
                Console.WriteLine($"四片允许各自独立；法兰保温按「不包」（§4.2v：包纤维使自给质量涨 23 %）");
                Console.WriteLine();

                // ── 形状库：圆盘半径 × 舌片长度 × 舌片末端半宽
                //    每个形状只解一次电流场，之后全解析
                var shapes = new List<(string name, FlangePlate g, DesignScreen.ShapeFactors sf)>();
                Console.WriteLine("阶段 A ①：提取形状因子（每个形状解一次壳电流场）…");
                foreach (double rd in new[] { 30.0, 34.0, 38.0, 44.0, 50.0, 60.0 })
                    foreach (double tabX in new[] { -50.0, -80.0, -120.0, -200.0 })
                        foreach (double halfW in new[] { 20.0, 30.0, 40.0, 55.0 })
                        {
                            var g = new FlangePlate
                            {
                                DiscRadiusMm = rd,
                                HoleRadiusMm = 26.0,
                                TabEndXMm = tabX,
                                TabEndHalfWidthMm = halfW,
                                ThicknessMm = 1.0,
                                ThickenedMm = 1.0,
                                InsulBoundaryXMm = 1e9      // 全裸（不包保温）
                            };
                            // 切点存在的条件：R ≤ |P|，P = (|tabX|, halfW)。梯形向外张开是允许的，
                            // 故不再要求 halfW < R（早先那条守卫把所有小圆盘都误杀了）。
                            if (rd >= Math.Sqrt(tabX * tabX + halfW * halfW)) continue;
                            if (rd <= 26.0 + 2.0) continue;  // 圆盘必须比管孔大出可用的一圈
                            try
                            {
                                var m = FlangeMesher.Build(g, 0, 2.0, 11.0, 45.0);
                                if (m.CellCount < 50) continue;
                                var s = DesignScreen.Extract(m, 1000.0, 1050.0, g.Tangent().X);
                                s.Name = $"Ø{2 * rd:0}/舌{-tabX:0}/半宽{halfW:0}";
                                shapes.Add((s.Name, g, s));
                            }
                            catch { /* 该形状网格退化，跳过 */ }
                        }
                Console.WriteLine($"  可用形状 {shapes.Count} 个");
                Console.WriteLine($"{"形状",22}{"净面积 mm²",12}{"ΣR",9}{"ΣJ",10}");
                foreach (var (nm, _, s) in shapes)
                    Console.WriteLine($"{nm,22}{s.AreaMm2,12:0}{s.ShapeR,9:0.000}{s.ShapeJ,10:0.0000}");
                Console.WriteLine();

                // ── 段电流：空管口径的稳态工作点（阶段 B 会用真实耦合解替换）
                double SegCurrent(double wallMm, double insMm, double tSet)
                {
                    var q = SegmentSolver.Clone(p);
                    q.Layer1.ThicknessMm = insMm; q.Layer1.Enabled = true;
                    double ri = q.TubeIdMm * 0.5e-3, w = wallMm * 1e-3, rOut = ri + w;
                    double areaM2 = Math.PI * (rOut * rOut - ri * ri);
                    bool anyIns = false;
                    foreach (var l in q.Layers) if (l.Enabled && l.ThicknessMm > 1e-6) anyIns = true;
                    double eps = anyIns ? q.OuterEmissivity : q.PtEmissivity;
                    double lossW = Insulation.CylinderLoss(tSet, q.TAmbC, rOut, q.Layers, eps,
                                       q.Posture == PtOptimize.Core.Orientation.Vertical,
                                       q.TubeLength, q.LossScale).QPerLength * q.TubeLength;
                    double rOhm = Materials.PtResistivity(tSet) * q.TubeLength / areaM2;
                    return Math.Sqrt(lossW / rOhm);
                }

                // ── 阶段 A ②：网格搜索
                Console.WriteLine("阶段 A ②：网格搜索（管壁 × 管保温 × 逐片形状 × 厚度）…");
                var cands = new List<(double mass, double wall, double ins, string[] shapeNames,
                                      FlangePlate[] plates, double[] amps, string note)>();

                foreach (double wall in new[] { 0.4, 0.5, 0.6, 0.8, 1.0 })
                    foreach (double ins in new[] { 2.5, 5.0, 10.0, 20.0, 40.0, 60.0 })
                    {
                        var q = SegmentSolver.Clone(p);
                        q.Layer1.ThicknessMm = ins; q.Layer1.Enabled = true;
                        q.WallMinMm = wall;

                        var amps = new double[nSeg];
                        for (int i = 0; i < nSeg; i++) amps[i] = SegCurrent(wall, ins, setpoints[i]);

                        double budget = DesignScreen.DrawBudgetW(q, wall, setpoints[0], 10.0);
                        double areaTubeMm2 = Math.PI * wall * (q.TubeIdMm + wall);
                        double tubeMass = areaTubeMm2 * q.TubeLengthMm * Materials.PtDensity * 1e-6 * nSeg;

                        var bestPlates = new FlangePlate[nSeg + 1];
                        var bestNames = new string[nSeg + 1];
                        double flangeMass = 0;
                        bool allOk = true;

                        for (int j2 = 0; j2 <= nSeg; j2++)
                        {
                            double iJoint = j2 == 0 ? amps[0]
                                          : j2 >= nSeg ? amps[nSeg - 1]
                                          : fShared * 0.5 * (amps[j2 - 1] + amps[j2]);
                            double tRoot = setpoints[Math.Min(j2, nSeg - 1)];
                            double qFlux = DesignScreen.PlateFluxWPerM2(q, tRoot, 0) * 1e-6;  // W/mm²
                            double rhoMm = Materials.PtResistivity(tRoot) * 1e3;

                            double bestM = double.MaxValue; FlangePlate? bestG = null; string bestN = "";
                            foreach (var (nm, g, s) in shapes)
                            {
                                // Φ=1 所需厚度 —— 自给点
                                double tReq = iJoint * iJoint * rhoMm * s.ShapeR / (2.0 * s.AreaMm2 * qFlux);
                                double loss = 2.0 * s.AreaMm2 * qFlux;
                                // ★ 靶不是 Φ=1（那是悬崖边，§4.2k），而是抽热取预算的一半 ——
                                //   从**安全侧**逼近：draw>0 表示法兰比管冷，方向安全。
                                //   draw = loss·(1 − tReq/t) = budget/2  ⇒  t = tReq/(1 − budget/(2·loss))
                                double t = tReq / Math.Max(1e-6, 1.0 - budget / (2.0 * loss));
                                if (t < tMinMm) continue;      // 撞 0.4 下界：该形状在此电流下太大，法兰必然过厚
                                double gen = iJoint * iJoint * rhoMm * s.ShapeR / t;
                                double draw = loss - gen;                       // >0 = 从管子抽热
                                if (draw <= 0 || draw > budget) continue;        // C2（单边）
                                // C1：法兰的平衡温度 ≤ 工作温度。draw>0 ⇒ Φ<1 ⇒ 平衡点在工作温度之下，
                                //     本条已被上面的 C2 单边判据覆盖。
                                //
                                // ⚠ 这里**不再**用逐点 J ≤ J_lim 当硬过滤：
                                //   Φ=1 的含义是**面均**发热 = 散热，即 J_rms = J_lim；
                                //   而 J_max/J_rms ≈ 2.17（孔周峰值）⇒ J_max²/J_rms² ≈ 3.7，
                                //   **任何 Φ≤1 的形状，逐点判据都必然超 3.7 倍** —— 两者互斥。
                                //   原因是逐点判据不含横向导热，而铂板翅片长度 ≈24 mm 与圆盘径向尺寸同量级，
                                //   孔周热点会被周围拉住。**局部峰值一律交给阶段 B 的壳温度场解判。**
                                double jmax = s.ShapeJ * iJoint / t;

                                // ⚠ 也不用「冷启比值 f²(R_f/C_f) ≤ R_t/C_t」当硬约束：
                                //   升温时管子是被刻意慢慢加热的，法兰会很快升到**自己的平衡点**并停住；
                                //   对 Φ≤1 的设计那个平衡点 ≤ 工作温度，并不烧。
                                //   照字面把「法兰 ≤ 管温」用在升温段等于禁止任何升温 —— 不是该规则的本意
                                //   （它是稳态规则，由 C2 的 ΔT>0 覆盖）。现役件烧毁是因为 Φ=1.51。
                                double m = s.MassG(t);
                                if (m < bestM)
                                {
                                    bestM = m; bestN = $"{nm}/t{t:0.00}";
                                    bestG = new FlangePlate
                                    {
                                        DiscRadiusMm = g.DiscRadiusMm, HoleRadiusMm = 26.0,
                                        TabEndXMm = g.TabEndXMm, TabEndHalfWidthMm = g.TabEndHalfWidthMm,
                                        ThicknessMm = t, ThickenedMm = t, InsulBoundaryXMm = 1e9
                                    };
                                }
                            }
                            if (bestG is null) { allOk = false; break; }
                            bestPlates[j2] = bestG; bestNames[j2] = bestN; flangeMass += bestM;
                        }
                        if (!allOk) continue;
                        cands.Add((tubeMass + flangeMass, wall, ins, bestNames, bestPlates, amps,
                                   $"管 {tubeMass:0} + 法兰 {flangeMass:0}"));
                    }

                cands.Sort((a, b) => a.mass.CompareTo(b.mass));
                Console.WriteLine($"  可行候选 {cands.Count} 个");
                Console.WriteLine();
                if (cands.Count == 0)
                {
                    Console.WriteLine("✗ 阶段 A 无可行解 —— 说明三条闭式判据在给定的形状库与下界内无交集。");
                    Console.WriteLine("  放宽方向：更小的圆盘/更短的舌片（减 A、减 ΣR）、更低的 f、更厚的管保温。");
                    return;
                }

                Console.WriteLine($"{"排名",5}{"总铂 g",10}{"管壁",7}{"纤维",7}{"入口片",26}{"共用片1",26}");
                for (int k = 0; k < Math.Min(10, cands.Count); k++)
                {
                    var c2 = cands[k];
                    Console.WriteLine($"{k + 1,5}{c2.mass,10:0}{c2.wall,7:0.0}{c2.ins,7:0.0}" +
                                      $"{c2.shapeNames[0],26}{c2.shapeNames[1],26}");
                }
                Console.WriteLine();

                // ── 阶段 B：第一性复核
                Console.WriteLine($"阶段 B：对前 {nVerify} 名跑完整耦合解（FV 电流场 + FV 温度场 + 段↔法兰迭代）");
                Console.WriteLine("        —— 报告值以此为准，阶段 A 只是筛选");
                Console.WriteLine();

                for (int k = 0; k < Math.Min(nVerify, cands.Count); k++)
                {
                    var c2 = cands[k];
                    Console.WriteLine($"───── 候选 #{k + 1}：管壁 {c2.wall:0.0} mm，纤维 {c2.ins:0.0} mm，" +
                                      $"阶段 A 估 {c2.mass:0} g");

                    var baseP = SegmentSolver.Clone(p);
                    baseP.Layer1.ThicknessMm = c2.ins; baseP.Layer1.Enabled = true;
                    baseP.WallMinMm = c2.wall;
                    baseP.FlangeInsulThickMm = 0; baseP.FlangeInsulated = false;

                    var lc = new LineCase
                    {
                        Base = baseP,
                        WallMm = c2.wall,
                        UseMeasuredCurrent = false,        // 由控温反算 —— 第一性
                        FlangePlates = c2.plates,
                        SetpointC = setpoints,
                        CheckRamp = true
                    };

                    LineResult lr;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try { lr = LineRunner.Run(lc, new SyncProgress<string>(_ => { })); }
                    catch (Exception ex) { Console.WriteLine($"  ✗ 求解异常：{ex.Message}"); continue; }
                    sw.Stop();
                    if (!lr.Ok) { Console.WriteLine($"  ✗ {lr.Message}"); continue; }
                    Console.WriteLine($"  用时 {sw.Elapsed.TotalMinutes:0.0} min   " +
                                      $"收敛 {(lr.Converged ? "✓" : "✗ —— 本候选的数不可用")}");
                    if (!lr.Converged) { foreach (var nt in lr.Notes) Console.WriteLine("    " + nt); continue; }

                    Console.WriteLine($"  {"段",6}{"电流 A",9}{"管 J",8}{"管根 °C",10}{"衔接温差 K",12}{"管重 g",9}");
                    foreach (var s2 in lr.Segments)
                        Console.WriteLine($"  {s2.Name,6}{s2.CurrentA,9:0}{s2.TubeJAPerMm2,8:0.00}" +
                            $"{s2.TRootC,10:0.0}{s2.RootDeltaK,12:+0.0;-0.0}{s2.MassG,9:0}");
                    Console.WriteLine($"  {"法兰",10}{"电流 A",9}{"J_max",8}{"Φ",8}{"抽热 W",9}" +
                                      $"{"最高 °C",10}{"铂重 g",9}");
                    foreach (var f2 in lr.Flanges)
                        Console.WriteLine($"  {f2.Name,10}{f2.CurrentA,9:0}{f2.JMaxAPerMm2,8:0.00}" +
                            $"{f2.Phi,8:0.000}{f2.QFromTubeW,9:+0;-0}{f2.TMaxC,10:0.0}{f2.MassG,9:0}");
                    Console.WriteLine($"  判据：");
                    foreach (var ck in lr.Checks)
                    {
                        string mark = ck.Kind == CheckKind.HardSafety ? "★"
                                    : ck.Kind == CheckKind.Target ? "○" : "·";
                        string vd = ck.Kind == CheckKind.Reference ? "—"
                                  : ck.Undetermined ? "?" : ck.Ok ? "✓" : "✗";
                        string act = double.IsNaN(ck.Actual) ? "达不到" : ck.Actual.ToString("0.000");
                        Console.WriteLine($"    {mark}{ck.Name,-20}{act,12} / {ck.Limit,-10:0.000} {vd}  {ck.Where}");
                    }
                    Console.WriteLine($"  ★ 整线总铂 {lr.TotalMassG:0} g（管 {lr.TubeMassG:0} + 法兰 {lr.FlangeMassG:0}）" +
                                      $"   基准 {lr.BaselineMassG:0} g   省 {lr.SavingPct:0.0} %");
                    Console.WriteLine($"    玻璃温降 模型 {lr.GlassDropModelK:0.0} / 实测 {lr.GlassDropMeasuredK:0.0} K");
                    Console.WriteLine();
                }
                return;
            }

            // --cli --timeone <file.3dm>   量一次整线耦合解在 .3dm 路径上要多久
            if (args.Contains("--timeone"))
            {
                int ti2 = Array.IndexOf(args, "--timeone");
                string tf2 = ti2 + 1 < args.Length && !args[ti2 + 1].StartsWith("--")
                             ? args[ti2 + 1] : Find3dm("Pt_Heater.3dm");
                var pt3 = SegmentSolver.Clone(p);
                pt3.Layer1.ThicknessMm = 10; pt3.Layer1.Enabled = true;
                pt3.WallMinMm = 0.4; pt3.FlangeInsulThickMm = 20; pt3.FlangeInsulated = true;
                pt3.BusbarClampTempC = 300;
                var lc3 = new LineCase
                {
                    Base = pt3, WallMm = 0.4, UseMeasuredCurrent = false, CheckRamp = false,
                    SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                    FlangeFile3dm = Enumerable.Repeat(tf2, 4).ToArray(), FlangeLayer = "法兰"
                };
                for (int rep = 0; rep < 2; rep++)
                {
                    var sw3 = System.Diagnostics.Stopwatch.StartNew();
                    var r3 = LineRunner.Run(lc3);
                    sw3.Stop();
                    Console.WriteLine($"第 {rep + 1} 次：{sw3.Elapsed.TotalSeconds:0.0} s　" +
                        (r3.Ok ? (r3.Converged ? "收敛" : "未收敛") : "失败 " + r3.Message) +
                        (r3.Ok ? $"　单元数 {r3.Flanges[0].CellCount}　总铂 {r3.TotalMassG:0} g" : ""));
                }
                Console.WriteLine("第 2 次明显更快 ⇒ 瓶颈在厚度场提取（已缓存）；否则瓶颈在网格/场解。");
                Console.WriteLine();
                Console.WriteLine("── 搜索期精度（粗网格 + 松耦合）");
                lc3.MeshFineMm = 4.0; lc3.MeshCoarseMm = 16.0;
                lc3.CoupleMaxRounds = 5; lc3.CoupleTolK = 4.0;
                for (int rep = 0; rep < 2; rep++)
                {
                    var sw4 = System.Diagnostics.Stopwatch.StartNew();
                    var r4 = LineRunner.Run(lc3);
                    sw4.Stop();
                    Console.WriteLine($"第 {rep + 1} 次：{sw4.Elapsed.TotalSeconds:0.0} s　" +
                        (r4.Ok ? $"单元数 {r4.Flanges[0].CellCount}　总铂 {r4.TotalMassG:0} g　" +
                                 $"最差段温差 {r4.Segments.Max(x => Math.Abs(x.RootDeltaK)):0.0} K"
                               : "失败"));
                }
                return;
            }

            // --cli --fidelity   同一组厚度，粗网格 vs 细网格，看管根温差差多少
            //
            // 「搜索期用粗网格提速」这个策略成不成立，取决于粗网格算出的**管根温差**
            // 是否与细网格一致（只需方向一致即可，不必数值相同）。实测发现差 280 K，
            // 故必须查清是网格还是耦合容差造成的 —— 这条命令把两个因素拆开。
            if (args.Contains("--fidelity"))
            {
                double wallF2 = 0.4, insF2 = 10.0, holeF2 = wallF2 + 25.0;
                var th4 = new[] { 0.400, 0.631, 0.527, 0.400 };

                var pF2 = SegmentSolver.Clone(p);
                pF2.Layer1.ThicknessMm = insF2; pF2.Layer1.Enabled = true;
                pF2.WallMinMm = wallF2;
                pF2.FlangeInsulThickMm = 20; pF2.FlangeInsulated = true;
                pF2.BusbarClampTempC = 300;

                FlangePlate MkF(double t) => new()
                {
                    DiscRadiusMm = 30, HoleRadiusMm = holeF2,
                    TabEndXMm = -50, TabEndHalfWidthMm = 20,
                    ThicknessMm = t, ThickenedMm = t, InsulBoundaryXMm = -1e9
                };

                Console.WriteLine("=== 精度对照：同一组厚度，只改网格与耦合容差 ===");
                Console.WriteLine($"形状 Ø60／舌50×20　厚度 {string.Join("/", th4.Select(x => x.ToString("0.000")))} mm");
                Console.WriteLine();
                Console.WriteLine($"{"网格 细/粗",14}{"耦合轮/容差",14}{"单元数",8}" +
                                  $"{"HC1 ΔT",10}{"HC2 ΔT",10}{"HC3 ΔT",10}{"法兰最高",10}{"总铂 g",9}{"用时 s",9}");

                foreach (var (mf, mc, cr, ctol, tag) in new[]
                {
                    (2.0, 11.0, 15, 1.0, "全精度"),
                    (4.0, 16.0, 15, 1.0, "只粗网格"),
                    (2.0, 11.0,  5, 4.0, "只松耦合"),
                    (4.0, 16.0,  5, 4.0, "搜索期设置"),
                })
                {
                    var lcF2 = new LineCase
                    {
                        Base = pF2, WallMm = wallF2, UseMeasuredCurrent = false, CheckRamp = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = th4.Select(MkF).ToArray(),
                        MeshFineMm = mf, MeshCoarseMm = mc,
                        CoupleMaxRounds = cr, CoupleTolK = ctol
                    };
                    var swF = System.Diagnostics.Stopwatch.StartNew();
                    var rF = LineRunner.Run(lcF2);
                    swF.Stop();
                    if (!rF.Ok) { Console.WriteLine($"{tag,14}  失败"); continue; }
                    Console.WriteLine($"{$"{mf:0.0}/{mc:0.0}",14}{$"{cr}/{ctol:0.0}",14}" +
                        $"{rF.Flanges[0].CellCount,8}" +
                        $"{rF.Segments[0].RootDeltaK,10:+0.0;-0.0}{rF.Segments[1].RootDeltaK,10:+0.0;-0.0}" +
                        $"{rF.Segments[2].RootDeltaK,10:+0.0;-0.0}{rF.Flanges.Max(f2 => f2.TMaxC),10:0}" +
                        $"{rF.TotalMassG,9:0}{swF.Elapsed.TotalSeconds,9:0.0}　{tag}" +
                        (rF.Converged ? "" : " ⚠未收敛"));
                }
                Console.WriteLine();
                Console.WriteLine("若「只粗网格」那行就偏得厉害 ⇒ 网格是主因，搜索期不能降网格；");
                Console.WriteLine("若「只松耦合」那行偏得厉害 ⇒ 是耦合没跑够，收紧容差即可。");
                return;
            }

            // --cli --trimauth   ★ 铜排夹持温度的**整定权限**：ΔT 对夹持温度的敏感度
            //
            // 我一直说「铜排空冷风量是现场整定的旋钮」，却从没算过它有多大权限。
            // 若 dΔT/dT_夹持 太小，这个旋钮就是摆设；太大则难以稳定控制。
            // 同时给出「法兰热量有多少比例经铜排走」—— 那决定了这个旋钮的物理杠杆。
            if (args.Contains("--trimauth"))
            {
                double wallA = 0.4, insA = 10.0, holeA = wallA + 25.0;
                var thA = new[] { 0.516, 0.855, 0.776, 0.426 };

                Console.WriteLine("=== 铜排夹持温度的整定权限 ===");
                Console.WriteLine("几何固定为交付方案，只改夹持温度，看管根温差怎么动。");
                Console.WriteLine();
                Console.WriteLine($"{"夹持 °C",9}{"HC1 ΔT",10}{"HC2 ΔT",10}{"HC3 ΔT",10}" +
                                  $"{"铜排带走 W",12}{"占发热比",10}{"法兰最高 °C",13}  收敛");

                var pts = new System.Collections.Generic.List<(double t, double d)>();
                foreach (double ct in new[] { 150.0, 300.0, 450.0, 600.0, 800.0 })
                {
                    var pA = SegmentSolver.Clone(p);
                    pA.Layer1.ThicknessMm = insA; pA.Layer1.Enabled = true;
                    pA.WallMinMm = wallA;
                    pA.FlangeInsulThickMm = 20; pA.FlangeInsulated = true;
                    pA.BusbarClampTempC = ct;
                    pA.BusbarClampLengthMm = 3.0;      // 与 --final 同口径，便于对照

                    FlangePlate MkA(double t) => new()
                    {
                        DiscRadiusMm = 30, HoleRadiusMm = holeA,
                        TabEndXMm = -50, TabEndHalfWidthMm = 20,
                        ThicknessMm = t, ThickenedMm = t, InsulBoundaryXMm = -1e9
                    };
                    var lcA = new LineCase
                    {
                        Base = pA, WallMm = wallA, UseMeasuredCurrent = false, CheckRamp = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = thA.Select(MkA).ToArray()
                    };
                    LineResult rA;
                    try { rA = LineRunner.Run(lcA); } catch { Console.WriteLine($"{ct,9:0}  失败"); continue; }
                    if (!rA.Ok) { Console.WriteLine($"{ct,9:0}  {rA.Message}"); continue; }
                    double qc = rA.Flanges.Sum(f8 => f8.QClampW);
                    double qg = rA.Flanges.Sum(f8 => f8.QGenW);
                    Console.WriteLine($"{ct,9:0}{rA.Segments[0].RootDeltaK,10:+0.0;-0.0}" +
                        $"{rA.Segments[1].RootDeltaK,10:+0.0;-0.0}{rA.Segments[2].RootDeltaK,10:+0.0;-0.0}" +
                        $"{qc,12:0}{qc / Math.Max(1e-9, qg) * 100,10:0}%{rA.Flanges.Max(f8 => f8.TMaxC),13:0}  " +
                        (rA.Converged ? "✓" : "✗"));
                    pts.Add((ct, rA.Segments.Min(x => x.RootDeltaK)));
                }

                Console.WriteLine();
                if (pts.Count >= 2)
                {
                    // 线性拟合斜率
                    double n = pts.Count, sx = pts.Sum(q => q.t), sy = pts.Sum(q => q.d);
                    double sxx = pts.Sum(q => q.t * q.t), sxy = pts.Sum(q => q.t * q.d);
                    double slope = (n * sxy - sx * sy) / Math.Max(1e-12, n * sxx - sx * sx);
                    Console.WriteLine($"★ 整定权限：dΔT/d夹持温度 ≈ **{slope:0.000} K/°C**");
                    if (Math.Abs(slope) > 1e-6)
                        Console.WriteLine($"  ⇒ 要把管根温差挪 5 K，需改夹持温度 {5 / Math.Abs(slope):0} °C");
                    Console.WriteLine();
                    Console.WriteLine("对照：管根温差对**法兰厚度**的斜率约 1300 K/mm ⇒ 5 K 对应 0.004 mm。");
                    Console.WriteLine("两者相比，看哪个在现场更可控 —— 这决定整定手段选谁。");
                }
                return;
            }

            // --cli --busbar   ★ 铜排校核（用户：「铜排计算也一样」）
            //
            // 用当前方案每片的电流与铜排热负荷，算出铜排要多大、压接要多长。
            if (args.Contains("--busbar"))
            {
                Console.WriteLine("=== 铜排校核 ===");
                Console.WriteLine("此前模型把铜排当**完美接触的定温边界**（假设你能做到 300 °C），");
                Console.WriteLine("这里把「要做到需要什么」算出来。");
                Console.WriteLine();

                // 当前方案（--final）的每片电流与铜排热负荷
                var rows = new (string name, double iA, double qW)[]
                {
                    ("入口",      685, 165),
                    ("HC1|HC2",  1099, 269),
                    ("HC2|HC3",   975, 225),
                    ("出口",      542, 119),
                };
                double tabW = 40, clampT = 300, sinkT = 60, lenToSink = 300;

                Console.WriteLine($"舌片宽 {tabW:0} mm　压接点维持 {clampT:0} °C　冷端 {sinkT:0} °C　" +
                                  $"压接点到冷端 {lenToSink:0} mm　两面夹");
                Console.WriteLine();
                Console.WriteLine($"{"片",10}{"电流A",8}{"带走W",8}" +
                                  $"{"载流需截面",12}{"导热需截面",12}{"取大",8}" +
                                  $"{"参考尺寸",14}{"压接面积",10}{"压接长",8}  控制项");

                foreach (var (nm, iA, qW) in rows)
                {
                    var b = BusbarSizing.Check(iA, qW, jBusAllow: 2.0, jContactAllow: 1.0,
                                tabWidthMm: tabW, lengthToSinkMm: lenToSink,
                                clampTempC: clampT, sinkTempC: sinkT, doubleSided: true);
                    // 参考尺寸：按宽 = 舌宽，算需要多厚
                    double thk = b.SectionRequiredMm2 / tabW;
                    Console.WriteLine($"{nm,10}{iA,8:0}{qW,8:0}" +
                        $"{b.SectionForCurrentMm2,12:0}{b.SectionForHeatMm2,12:0}{b.SectionRequiredMm2,8:0}" +
                        $"{$"{tabW:0}×{thk:0.0}",14}{b.ContactAreaMm2,10:0}{b.ContactLenMm,8:0}  " +
                        (b.SectionForHeatMm2 > b.SectionForCurrentMm2 ? "导热" : "载流"));
                }

                Console.WriteLine();
                Console.WriteLine("── 敏感性：压接点温度定得越低，导热需要的截面越大");
                Console.WriteLine($"{"压接温度 °C",13}{"ΔT到冷端",11}{"共用片导热需截面 mm²",22}{"对应 40 mm 宽的厚度",20}");
                foreach (double ct in new[] { 200.0, 300.0, 400.0, 500.0 })
                {
                    var b = BusbarSizing.Check(1099, 269, 2.0, 1.0, tabW, lenToSink, ct, sinkT, true);
                    Console.WriteLine($"{ct,13:0}{ct - sinkT,11:0}{b.SectionForHeatMm2,22:0}" +
                                      $"{b.SectionForHeatMm2 / tabW,20:0.0}");
                }

                Console.WriteLine();
                Console.WriteLine("★ 读法：");
                Console.WriteLine("· 「导热需截面」通常大于「载流需截面」⇒ **铜排是被散热需求定尺寸的，不是被电流**");
                Console.WriteLine("· 压接长由界面电流密度 ≤1 A/mm² 定，与热学无关，但它决定舌片要多长");
                Console.WriteLine("· 压接点温度是**现场整定旋钮**：定得低 ⇒ 法兰更凉、管根温差更大，且铜排要更粗");
                Console.WriteLine();
                Console.WriteLine("⚠ 本校核未含：接触热阻（压紧力/表面状态）、铜排自身对流散热、");
                Console.WriteLine("  铂-铜异种金属在 300 °C 长期接触的扩散/氧化。这三条需实测或选型时另行确认。");
                return;
            }

            // --cli --auto2   ★ 逐片夹持 + 自动定厚 同时放开
            //
            // --perplate 固定厚度 0.4 只扫夹持，三段 ΔT 落在 +33/−88/+106，一个旋钮压不平。
            // 本命令把厚度也交给自动定厚，逐片夹持取 §4.3c 的结论：
            //   端片自由端（它连自己散热都不够，再夹冷只能抽管子）
            //   共用片夹冷（它发热远超自身散热，多余的要带走）
            if (args.Contains("--auto2"))
            {
                double wallA2 = 0.4, insA2 = 10.0, holeA2 = wallA2 + 25.0;

                Console.WriteLine("=== 逐片夹持 + 自动定厚 ===");
                Console.WriteLine("端片自由端；共用片夹冷（扫）；四片厚度由自动定厚求");
                Console.WriteLine();
                Console.WriteLine($"{"盘Ø",6}{"舌长",6}{"舌半宽",7}{"舌厚",6}{"共用夹持",9}" +
                                  $"{"四片厚度 mm",24}{"ΔT范围",15}{"总铂g",8}  判定");

                foreach (var (disc2, tabL6, hw6, tt6) in new[]
                {
                    (30.0, 90.0, 30.0, 0.8),
                    (30.0, 90.0, 20.0, 0.8),
                    (30.0, 90.0, 20.0, 1.5),
                    (36.0, 90.0, 20.0, 0.8),
                })
                    foreach (double ctS2 in new[] { 300.0, 600.0 })
                    {
                        var pA2 = SegmentSolver.Clone(p);
                        pA2.Layer1.ThicknessMm = insA2; pA2.Layer1.Enabled = true;
                        pA2.WallMinMm = wallA2;
                        pA2.FlangeInsulThickMm = 20; pA2.FlangeInsulated = true;
                        pA2.BusbarClampLengthMm = 40;

                        FlangePlate MkA2(double td) => new()
                        {
                            DiscRadiusMm = disc2, HoleRadiusMm = holeA2,
                            TabEndXMm = -tabL6, TabEndHalfWidthMm = hw6,
                            ThicknessMm = td, ThickenedMm = td, TabThicknessMm = tt6,
                            InsulBoundaryXMm = double.NaN
                        };
                        var lcA2 = new LineCase
                        {
                            Base = pA2, WallMm = wallA2, UseMeasuredCurrent = false, CheckRamp = false,
                            SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                            FlangePlates = new[] { MkA2(0.5), MkA2(0.5), MkA2(0.5), MkA2(0.5) },
                            ClampTempC = new[] { -1.0, ctS2, ctS2, -1.0 }
                        };
                        var rA2 = FlangeAutoSizer.SolveAuto(lcA2, MkA2,
                                      new[] { 0.5, 0.5, 0.5, 0.5 }, new FlangeAutoSizer.Options(),
                                      new SyncProgress<string>(_ => { }), default);
                        if (rA2.Line is not { Ok: true } lr6)
                        { Console.WriteLine($"{2 * disc2,6:0}{tabL6,6:0}{hw6,7:0}{tt6,6:0.0}{ctS2,9:0}" +
                                            $"   ✗ {rA2.Message}"); continue; }
                        double dmin2 = lr6.Segments.Min(x => x.RootDeltaK);
                        double dmax2 = lr6.Segments.Max(x => x.RootDeltaK);
                        bool ok2 = rA2.Converged && lr6.Converged && dmin2 > 0 && dmax2 <= 10;
                        Console.WriteLine($"{2 * disc2,6:0}{tabL6,6:0}{hw6,7:0}{tt6,6:0.0}{ctS2,9:0}" +
                            $"{string.Join("/", rA2.ThicknessMm.Select(x => x.ToString("0.00"))),24}" +
                            $"{$"{dmin2:+0.0;-0.0}~{dmax2:+0.0;-0.0}",15}{lr6.TotalMassG,8:0}  " +
                            (ok2 ? "✓ 全过" : dmin2 <= 0 ? "✗倒灌" : $"✗最大{dmax2:0}K"));
                    }
                Console.WriteLine();
                Console.WriteLine("★ 若仍无解且厚度顶在下界 ⇒ 面积太大，须回到几何（缩盘/窄舌）或电流（改接线）。");
                return;
            }

            // --cli --perplate   ★ 逐片夹持温度独立：端片自由端 + 共用片夹冷
            //
            // §4.3c：共用片自由端时发热 490 W 远超自身散热 ⇒ 往管子灌（ΔT 变负）；
            //        端片自由端时发热 156 W 仍不够自己散热 ⇒ 仍抽管子（ΔT 正）。
            // 两者需要相反的处置 ⇒ 夹持温度必须逐片给。
            if (args.Contains("--perplate"))
            {
                double wallP = 0.4, insP = 10.0, holeP = wallP + 25.0;
                double discP = 30.0, tabLP = 90.0, hwP = 30.0, ttP = 0.8;

                Console.WriteLine("=== 逐片夹持温度：端片自由端 + 共用片夹冷 ===");
                Console.WriteLine($"几何 盘Ø{2 * discP:0}／舌 {tabLP:0}×{2 * hwP:0}×{ttP:0.00}／厚度全 0.4 mm");
                Console.WriteLine("端片固定自由端（−1）；只扫共用片的夹持温度");
                Console.WriteLine();
                Console.WriteLine($"{"共用片夹持",12}{"HC1 ΔT",10}{"HC2 ΔT",10}{"HC3 ΔT",10}" +
                                  $"{"共用发热",10}{"共用铜排",10}{"端片ΔT贡献",12}  评价");

                foreach (double ctS in new[] { 200.0, 400.0, 600.0, 800.0, -1.0 })
                {
                    var pP = SegmentSolver.Clone(p);
                    pP.Layer1.ThicknessMm = insP; pP.Layer1.Enabled = true;
                    pP.WallMinMm = wallP;
                    pP.FlangeInsulThickMm = 20; pP.FlangeInsulated = true;
                    pP.BusbarClampLengthMm = 40;

                    FlangePlate MkP(double td) => new()
                    {
                        DiscRadiusMm = discP, HoleRadiusMm = holeP,
                        TabEndXMm = -tabLP, TabEndHalfWidthMm = hwP,
                        ThicknessMm = td, ThickenedMm = td, TabThicknessMm = ttP,
                        InsulBoundaryXMm = double.NaN
                    };
                    var lcP = new LineCase
                    {
                        Base = pP, WallMm = wallP, UseMeasuredCurrent = false, CheckRamp = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = new[] { MkP(0.4), MkP(0.4), MkP(0.4), MkP(0.4) },
                        ClampTempC = new[] { -1.0, ctS, ctS, -1.0 }   // 端片自由、共用夹冷
                    };
                    LineResult rP;
                    try { rP = LineRunner.Run(lcP); }
                    catch (Exception ex) { Console.WriteLine($"{ctS,12:0}  异常 {ex.Message}"); continue; }
                    if (!rP.Ok) { Console.WriteLine($"{ctS,12:0}  {rP.Message}"); continue; }

                    double dmin = rP.Segments.Min(x => x.RootDeltaK);
                    double dmax = rP.Segments.Max(x => x.RootDeltaK);
                    Console.WriteLine($"{(ctS < 0 ? "自由端" : ctS.ToString("0")),12}" +
                        $"{rP.Segments[0].RootDeltaK,10:+0.0;-0.0}{rP.Segments[1].RootDeltaK,10:+0.0;-0.0}" +
                        $"{rP.Segments[2].RootDeltaK,10:+0.0;-0.0}" +
                        $"{rP.Flanges[1].QGenW,10:0}{rP.Flanges[1].QClampW,10:0}" +
                        $"{rP.Flanges[0].QFromTubeW,12:+0;-0}  " +
                        (dmin > 0 && dmax <= 10 ? "✓ 全过" :
                         dmin <= 0 ? "✗ 有段倒灌" : $"✗ 最大 {dmax:0} K"));
                }
                Console.WriteLine();
                Console.WriteLine("★ 找「三段都为正且都 ≤10」的那一档。若找不到，说明厚度也要跟着调 ——");
                Console.WriteLine("  但厚度已顶在 0.4 下界，那就要回到几何（缩面积）或电流（改接线）。");
                return;
            }

            // --cli --meshcost   ★ 细网格半径的等价性验证：长舌片能不能交给粗网格
            //
            // 实测 --asym 单格 10 min（预估 1.5 min，超 6.7 倍），排查是舌片 90mm 使单元数暴涨。
            // fineRadius 控制「多大半径内用细网格」。孔周必须细（§2.5 的教训），
            // 但**长舌片属于远场**，粗一点应无害 —— 必须先验证，不能直接改。
            if (args.Contains("--meshcost"))
            {
                var pM = SegmentSolver.Clone(p);
                pM.Layer1.ThicknessMm = 10; pM.Layer1.Enabled = true;
                pM.WallMinMm = 0.4;
                pM.FlangeInsulThickMm = 20; pM.FlangeInsulated = true;
                pM.BusbarClampLengthMm = 40;

                Console.WriteLine("=== 细网格半径的等价性验证 ===");
                Console.WriteLine("孔 R25.4／盘 Ø60／共用舌 90×60×0.8；只改 fineRadius");
                Console.WriteLine($"{"fineR",8}{"单元数",8}{"HC1 ΔT",10}{"HC2 ΔT",10}{"HC3 ΔT",10}" +
                                  $"{"总铂g",8}{"用时s",8}");
                foreach (double fr in new[] { 45.0, 35.0, 30.0 })
                {
                    FlangePlate MkM(double td, double tabLen) => new()
                    {
                        DiscRadiusMm = 30, HoleRadiusMm = 25.4,
                        TabEndXMm = -tabLen, TabEndHalfWidthMm = 30,
                        ThicknessMm = td, ThickenedMm = td, TabThicknessMm = 0.8,
                        InsulBoundaryXMm = double.NaN
                    };
                    var lcM = new LineCase
                    {
                        Base = pM, WallMm = 0.4, UseMeasuredCurrent = false, CheckRamp = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        ClampTempC = new[] { -1.0, 300.0, 300.0, -1.0 },
                        MeshFineRadiusMm = fr,
                        FlangePlates = new[] { MkM(0.4, 50), MkM(0.4, 90), MkM(0.4, 90), MkM(0.4, 50) }
                    };
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    LineResult rM;
                    try { rM = LineRunner.Run(lcM); } catch (Exception ex)
                    { Console.WriteLine($"{fr,8:0}  异常 {ex.Message}"); continue; }
                    sw.Stop();
                    if (!rM.Ok) { Console.WriteLine($"{fr,8:0}  {rM.Message}"); continue; }
                    Console.WriteLine($"{fr,8:0}{rM.Flanges[1].CellCount,8}" +
                        $"{rM.Segments[0].RootDeltaK,10:+0.0;-0.0}{rM.Segments[1].RootDeltaK,10:+0.0;-0.0}" +
                        $"{rM.Segments[2].RootDeltaK,10:+0.0;-0.0}{rM.TotalMassG,8:0}" +
                        $"{sw.Elapsed.TotalSeconds,8:0.0}");
                }
                Console.WriteLine();
                Console.WriteLine("★ 若 ΔT 三段都基本不变而用时大降 ⇒ 长舌片可以交给粗网格，提速是安全的。");
                Console.WriteLine("  若 ΔT 变了 ⇒ 舌片也参与决定管根温差，不能粗 —— 那就只能减格数。");
                return;
            }

            // --cli --asym   ★★ 端片与共用片**形状也不同**：端片短舌自由端、共用片长舌夹冷
            //
            // 依据（§4.3c 的数据）：
            //   · 共用片夹持只影响 HC2；HC1/HC3 由端片决定，而端片已自由端+0.4mm，调无可调
            //   · 端片账：发热 156 W < 散热 237 W，差 81 W 只能从管子补
            //   · **端片自由端 ⇒ 不需要 45 mm「有效导热长」**（那条来自限制流向冷夹持的热，
            //     没有冷源就不存在）⇒ 舌片只留压接需要的长度 ⇒ 面积小 ⇒ 散热小
            if (args.Contains("--asym"))
            {
                double wallA2 = 0.4, insA2 = 10.0, holeA2 = wallA2 + 25.0;

                Console.WriteLine("=== 端片/共用片 形状与夹持都独立 ===");
                Console.WriteLine("端片：短舌（只留压接长）+ 自由端　共用片：长舌 + 夹冷");
                Console.WriteLine("四片厚度由自动定厚求；管壁 0.4／管纤维 10／圆盘包 20、舌片裸露");
                Console.WriteLine();
                Console.WriteLine($"{"端片舌长",9}{"共用舌长",9}{"共用夹持",9}" +
                                  $"{"四片厚度 mm",24}{"ΔT范围",15}{"总铂g",8}  判定");

                foreach (double endTab in new[] { 50.0, 60.0 })
                    foreach (double shTab in new[] { 90.0, 130.0 })
                        foreach (double shClamp in new[] { 300.0, 600.0 })
                        {
                            var pA2 = SegmentSolver.Clone(p);
                            pA2.Layer1.ThicknessMm = insA2; pA2.Layer1.Enabled = true;
                            pA2.WallMinMm = wallA2;
                            pA2.FlangeInsulThickMm = 20; pA2.FlangeInsulated = true;
                            pA2.BusbarClampLengthMm = 40;

                            FlangePlate MkA2(double td, double tabLen) => new()
                            {
                                DiscRadiusMm = 30, HoleRadiusMm = holeA2,
                                TabEndXMm = -tabLen, TabEndHalfWidthMm = 30,
                                ThicknessMm = td, ThickenedMm = td, TabThicknessMm = 0.8,
                                InsulBoundaryXMm = double.NaN
                            };
                            // 四片：端片用 endTab，共用片用 shTab
                            Func<double[], FlangePlate[]> mk4 = t => new[]
                            {
                                MkA2(t[0], endTab), MkA2(t[1], shTab),
                                MkA2(t[2], shTab), MkA2(t[3], endTab)
                            };

                            var lcA2 = new LineCase
                            {
                                Base = pA2, WallMm = wallA2, UseMeasuredCurrent = false,
                                CheckRamp = false, SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                                ClampTempC = new[] { -1.0, shClamp, shClamp, -1.0 },
                                FlangePlates = mk4(new[] { 0.4, 0.8, 0.8, 0.4 })
                            };

                            // 手工阻尼牛顿（因为 makePlate 要按片给不同舌长，SolveAuto 的单参签名不够）
                            var t4 = new[] { 0.5, 0.9, 0.8, 0.5 };
                            LineResult? best = null;
                            for (int it = 0; it < 30; it++)
                            {
                                lcA2.FlangePlates = mk4(t4);
                                LineResult rA;
                                try { rA = LineRunner.Run(lcA2); } catch { break; }
                                if (!rA.Ok) break;
                                best = rA;
                                var er = rA.Segments.Select(x => x.RootDeltaK - 5.0).ToArray();
                                if (er.Max(Math.Abs) < 2.0) break;
                                int pin = 0;
                                for (int j = 0; j < 4; j++)
                                {
                                    double e = j == 0 ? er[0] : j >= 3 ? er[2] : 0.5 * (er[j - 1] + er[j]);
                                    double want = t4[j] * Math.Exp(Math.Clamp(-0.6 * e / 800, -0.35, 0.35));
                                    double nx = Math.Clamp(want, 0.4, 6.0);
                                    if ((want < 0.4 && t4[j] <= 0.4001) || (want > 6 && t4[j] >= 5.999)) pin++;
                                    t4[j] = nx;
                                }
                                if (pin == 4) break;
                            }
                            if (best is null) { Console.WriteLine($"{endTab,9:0}{shTab,9:0}{shClamp,9:0}  求解失败"); continue; }
                            double dmin = best.Segments.Min(x => x.RootDeltaK);
                            double dmax = best.Segments.Max(x => x.RootDeltaK);
                            bool ok = best.Converged && dmin > 0 && dmax <= 10;
                            Console.WriteLine($"{endTab,9:0}{shTab,9:0}{shClamp,9:0}" +
                                $"{string.Join("/", t4.Select(x => x.ToString("0.00"))),24}" +
                                $"{$"{dmin:+0.0;-0.0}~{dmax:+0.0;-0.0}",15}{best.TotalMassG,8:0}  " +
                                (ok ? "✓" : dmin <= 0 ? "✗倒灌" : $"✗最大{dmax:0}K"));
                        }
                Console.WriteLine();
                Console.WriteLine("★ 端片短舌是这一步的关键：自由端没有冷源，就不需要长舌去拉开导热路径。");
                return;
            }

            // --cli --freeend   ★ 端片改自由端（不夹冷）：能否解开「自给 vs 电流密度」的死结
            //
            // 我一路默认四片都夹冷 300 °C —— **那是我加的假设，不是用户给的条件**。
            // 现役铜排没有水冷（BusbarClampTempC = −1，自由辐射端）。
            // 自由端的两个效果，第二个才是主要的：
            //   ① Q_夹持 归零
            //   ② **舌片能跑到高温 ⇒ ρe 上升 ⇒ 同样电流下发热大幅增加**
            if (args.Contains("--freeend"))
            {
                double wallF4 = 0.4, insF4 = 10.0, holeF4 = wallF4 + 25.0;
                double discF4 = 30.0, tabLF = 90.0, hwF4 = 30.0, ttF4 = 0.8;

                Console.WriteLine("=== 端片自由端 vs 夹冷：完整耦合解对照 ===");
                Console.WriteLine($"几何 盘Ø{2 * discF4:0}／舌 {tabLF:0}×{2 * hwF4:0}×{ttF4:0.00}／圆盘包20、舌片裸露");
                Console.WriteLine("四片厚度固定 0.4 mm（工艺下界），只改夹持条件，看能量账怎么变");
                Console.WriteLine();
                Console.WriteLine($"{"夹持条件",14}{"片",10}{"发热W",8}{"表面散热",10}" +
                                  $"{"管孔流入",10}{"铜排带走",10}{"舌端°C",9}{"残差",8}");

                foreach (var (tag, clampT) in new[]
                {
                    ("夹冷 300 °C", 300.0),
                    ("夹冷 600 °C", 600.0),
                    ("**自由端**", -1.0),
                })
                {
                    var pF4 = SegmentSolver.Clone(p);
                    pF4.Layer1.ThicknessMm = insF4; pF4.Layer1.Enabled = true;
                    pF4.WallMinMm = wallF4;
                    pF4.FlangeInsulThickMm = 20; pF4.FlangeInsulated = true;
                    pF4.BusbarClampTempC = clampT; pF4.BusbarClampLengthMm = 40;

                    FlangePlate MkF4(double td) => new()
                    {
                        DiscRadiusMm = discF4, HoleRadiusMm = holeF4,
                        TabEndXMm = -tabLF, TabEndHalfWidthMm = hwF4,
                        ThicknessMm = td, ThickenedMm = td, TabThicknessMm = ttF4,
                        InsulBoundaryXMm = double.NaN
                    };
                    var lcF4 = new LineCase
                    {
                        Base = pF4, WallMm = wallF4, UseMeasuredCurrent = false, CheckRamp = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = new[] { MkF4(0.4), MkF4(0.4), MkF4(0.4), MkF4(0.4) }
                    };
                    LineResult rF4;
                    try { rF4 = LineRunner.Run(lcF4); }
                    catch (Exception ex) { Console.WriteLine($"{tag,14}  异常 {ex.Message}"); continue; }
                    if (!rF4.Ok) { Console.WriteLine($"{tag,14}  {rF4.Message}"); continue; }

                    bool first = true;
                    foreach (var f12 in rF4.Flanges)
                    {
                        Console.WriteLine($"{(first ? tag : ""),14}{f12.Name,10}{f12.QGenW,8:0}" +
                            $"{f12.QLossW,10:0}{f12.QFromTubeW,10:+0;-0}{f12.QClampW,10:0}" +
                            $"{f12.TTabEndC,9:0}{f12.EnergyResidualW,8:+0.0;-0.0}");
                        first = false;
                    }
                    Console.WriteLine($"{"",14}管根温差 " + string.Join(" / ",
                        rF4.Segments.Select(x => x.RootDeltaK.ToString("+0.0;-0.0"))) + " K" +
                        (rF4.Converged ? "　✓" : "　⚠未收敛"));
                    Console.WriteLine();
                }
                Console.WriteLine("★ 关键看两件事：① 端片发热是否升到能盖住自己的散热；");
                Console.WriteLine("  ② 管根温差是否从 +200K 量级掉下来。若掉下来，说明「必须夹冷」是我的错误假设。");
                return;
            }

            // --cli --canwork   ★ 闭式可行性图：这片法兰在给定电流下**有没有可能**自给
            //
            // 目的：**搜索前先判有没有解**。本轮已在注定失败的搜索上烧掉数小时，
            // 而这个判断只要一秒 —— 全部闭式，不解场。
            //
            // 一片法兰不从管子抽热的条件（§4.3a 的能量对账口径）：
            //     自身发热  ≥  表面散热 + 铜排带走
            //     I²·ρe·ΣR/t  ≥  2·A·q″  +  Q_clamp
            // 其中 Q_clamp 由夹持温度与舌片截面定，与厚度弱相关。
            // 把它整理成对**厚度**的不等式，看有没有落在 [0.4, 6] 内的解。
            if (args.Contains("--canwork"))
            {
                double wallC2 = 0.4, insC2 = 10.0, holeC2 = wallC2 + 25.0;
                var pC2 = SegmentSolver.Clone(p);
                pC2.Layer1.ThicknessMm = insC2; pC2.Layer1.Enabled = true;
                pC2.WallMinMm = wallC2;
                pC2.FlangeInsulThickMm = 20; pC2.FlangeInsulated = true;

                // ★★ 标定系数：闭式用工作温度的 ρe 算整片发热，而夹持把大部分板拖冷
                //   （90mm 舌片有 40mm 按在 300°C），实际 ρe 只有工作温度的约 1/3。
                //   实测对照（Ø72/舌90×60×0.8/压接40/夹300）：
                //     闭式发热 323 W  vs  完整解 106 W  ⇒ 高估 3.0 倍
                //   故乘 0.33。**该系数随夹持长度/温度/舌长变化，配置大改后必须重标。**
                //   ⇒ 闭式只作**必要条件**：说不行就一定不行；说行还必须完整解复核。
                const double genCalib = 0.33;

                Console.WriteLine("=== 闭式可行性：法兰能否自给（不抽管子的热）===");
                Console.WriteLine($"⚠ 发热按实测标定系数 {genCalib:0.00} 折算（闭式假设整片在工作温度，");
                Console.WriteLine("  而夹持把板拖冷 ⇒ ρe 只有工作温度的约 1/3）。**本判据只作必要条件。**");
                Console.WriteLine("条件：I²·ρe·ΣR/t ≥ 2·A·q″ + Q_夹持");
                Console.WriteLine("⇒ t ≤ I²·ρe·ΣR / (2·A·q″ + Q_夹持)　**上界**；再与工艺下界 0.4 比");
                Console.WriteLine("（t 越小发热越多 ⇒ 自给要求的是厚度**上界**）");
                Console.WriteLine();

                double tWork2 = 1150;
                double rhoMm2 = Materials.PtResistivity(tWork2) * 1e3;
                double kMm = Materials.PtThermalK(tWork2) * 1e-3;

                // 端片可行边界搜索：窄舌 + 舌片保温是两个方向
                foreach (var (nm, disc, tabL5, hw4, ttab) in new[]
                {
                    ("窄舌 半宽15", 30.0, 90.0, 15.0, 0.8),
                    ("窄舌 半宽10", 30.0, 90.0, 10.0, 0.8),
                    ("窄舌长 15×130", 30.0, 130.0, 15.0, 0.8),
                    ("极窄 半宽8", 30.0, 90.0, 8.0, 0.8),
                    ("窄薄 15×0.5", 30.0, 90.0, 15.0, 0.5),
                })
                {
                    var g5 = new FlangePlate
                    {
                        DiscRadiusMm = disc, HoleRadiusMm = holeC2,
                        TabEndXMm = -tabL5, TabEndHalfWidthMm = hw4,
                        ThicknessMm = 1.0, ThickenedMm = 1.0,
                        TabThicknessMm = ttab, InsulBoundaryXMm = double.NaN
                    };
                    ShellMesh m5;
                    try { m5 = FlangeMesher.Build(g5, 0, 2.0, 11.0, 45.0, 40.0); }
                    catch { Console.WriteLine($"{nm,10}  网格失败"); continue; }
                    var sf5 = DesignScreen.Extract(m5, 1000.0, 1050.0, g5.Tangent().X);
                    double area = sf5.AreaMm2;
                    double qIns = DesignScreen.PlateFluxWPerM2(pC2, tWork2, 20.0) * 1e-6;   // 圆盘包
                    double qBare = DesignScreen.PlateFluxWPerM2(pC2, 800, 0) * 1e-6;         // 舌片裸
                    double qTabIns = DesignScreen.PlateFluxWPerM2(pC2, 800, 10.0) * 1e-6;    // 舌片包10mm
                    double aDisc = Math.PI * (disc * disc - holeC2 * holeC2);
                    double aTab = Math.Max(0, area - aDisc);
                    double lossW = 2 * (aDisc * qIns + aTab * qBare);
                    double lossWIns = 2 * (aDisc * qIns + aTab * qTabIns);   // 舌片也包保温

                    Console.WriteLine($"── {nm}：盘Ø{2 * disc:0}／舌{tabL5:0}×{2 * hw4:0}×{ttab:0.00}" +
                                      $"　净面积 {area:0} mm²　ΣR={sf5.ShapeR:0.000}　表面散热 {lossW:0} W");
                    Console.WriteLine($"   {"夹持°C",8}{"Q_夹持W",10}{"端片687A: t上界",18}" +
                                      $"{"共用1105A: t上界",18}{"端片(舌包10)",16}  判定（对下界 0.4）");
                    foreach (double ct2 in new[] { 300.0, 500.0, 700.0, 900.0 })
                    {
                        // 铜排带走：舌片截面沿有效长导到夹持温度
                        double effL2 = tabL5 - 40;
                        double qClamp = kMm * (2 * hw4 * ttab) / Math.Max(1, effL2) * (tWork2 - ct2);
                        double tEnd = genCalib * 687.0 * 687.0 * rhoMm2 * sf5.ShapeR / (lossW + qClamp);
                        double tSh = genCalib * 1105.0 * 1105.0 * rhoMm2 * sf5.ShapeR / (lossW + qClamp);
                        double tEndIns = genCalib * 687.0 * 687.0 * rhoMm2 * sf5.ShapeR / (lossWIns + qClamp);
                        Console.WriteLine($"   {ct2,8:0}{qClamp,10:0}{tEnd,18:0.000}{tSh,18:0.000}" +
                            $"{tEndIns,16:0.000}  " +
                            (tEnd >= 0.4 ? "✓ 端片裸露即可"
                             : tEndIns >= 0.4 ? "◐ 端片需包舌片保温"
                             : "✗ 端片无解"));
                    }
                    Console.WriteLine();
                }
                Console.WriteLine("★ 读法：t 上界 < 0.4 ⇒ 即使做到工艺最薄也发不出足够的热 ⇒ **注定抽管子的热**。");
                Console.WriteLine("  t 上界 ≥ 0.4 ⇒ **可能**可行，仍须完整解复核（闭式是必要条件不是充分条件）。");
                Console.WriteLine("  这一步只要一秒，**必须在动辄几小时的搜索之前跑**。");
                return;
            }

            // --cli --endplate   ★★ 端片可行性图：**分区**能量账（圆盘 / 舌片）+ 四条杠杆各扫一遍
            //
            // 为什么要这条：§4.3b–4.3d 一路卡在端片，但手上只有「整片发热 106 < 散热 138」
            // 这一个数 —— 它指不出该动圆盘还是舌片，而两者的杠杆方向相反：
            //   · 圆盘亏 ⇒ 缩盘径 / 加厚保温（圆盘紧贴管子，包保温无害，§4.3a）
            //   · 舌片亏 ⇒ **窄舌 + 等比加厚**：J = I/(w·t) 不变 ⇒ 发热上限不变，
            //     而裸露散热 ∝ w ⇒ 直接减半。§4.3b 的闭式判据把两区的散热并进一个 ΣR，
            //     得出「窄舌反而更差」，那个结论**只在圆盘主导散热时成立**，必须实测分区来判。
            //
            // 单片解、定管根温度 ⇒ 一格约 1 s（整线解 20 s），故可以真的扫。
            // 电流场与 LineRunner 同口径（按控温点的均匀 ρe 解一次，不与温度场迭代），
            // 保证这里的数与 --balance / --perplate 可直接对比。
            if (args.Contains("--endplate"))
            {
                double wallE9 = 0.4, insE9 = 10.0, holeE9 = wallE9 + 25.0;
                double tRootE9 = 1150.0;                 // HC1 控温点 = 入口端片的管根温度
                double iEndE9 = 687.0, iShareE9 = 1105.0; // §4.3a 实算：端片 / 共用片电流

                var pE9 = SegmentSolver.Clone(p);
                pE9.Layer1.ThicknessMm = insE9; pE9.Layer1.Enabled = true;
                pE9.WallMinMm = wallE9;
                pE9.FlangeInsulThickMm = 20; pE9.FlangeInsulated = true;
                pE9.BusbarClampLengthMm = 40;

                // 一次单片解：返回分区账 + 两区的 J 峰值 + 舌片局部稳定裕度
                (ShellThermalResult Th, double JTab, double JDisc, double MassG, double JStabTab)
                    SolveOneE9(FlangePlate g, double iA, double clampC)
                {
                    var mesh = FlangeMesher.Build(g, 0, 2.0, 11.0, 45.0, pE9.BusbarClampLengthMm);
                    var sc = ShellCurrent.Solve(mesh, iA,
                                Materials.PtResistivity(tRootE9) * 1e3, tRootE9);
                    var p2 = SegmentSolver.Clone(pE9);
                    p2.TSetC = tRootE9;
                    p2.BusbarClampTempC = clampC;
                    double xt = g.Tangent().X;
                    var th = ShellThermal.Solve(mesh, sc.JMagAPerMm2, p2, tRootE9,
                                                g.InsulBoundaryXResolved, symmetricInsul: g.TwoTabs,
                                                tabBoundaryX: xt,
                                                tabInsulThickMm: g.TabInsulThickMm);
                    // 舌片上「J 最高的那个单元」才是判局部失稳的点，且要用它自己的温度与厚度
                    double jT = 0, jD = 0, tAtJ = 0, thickAtJ = g.TabThicknessMm;
                    for (int i = 0; i < mesh.CellCount; i++)
                    {
                        bool onTab = g.TwoTabs ? Math.Abs(mesh.Centroid[i].X) > Math.Abs(xt)
                                               : mesh.Centroid[i].X < xt;
                        if (onTab)
                        {
                            if (sc.JMagAPerMm2[i] > jT)
                            { jT = sc.JMagAPerMm2[i]; tAtJ = th.T[i]; thickAtJ = mesh.Thickness[i]; }
                        }
                        else jD = Math.Max(jD, sc.JMagAPerMm2[i]);
                    }
                    double latMm = LocalStability.TabHalfSpanMm(
                                       -g.TabEndXMm, pE9.BusbarClampLengthMm, xt);
                    double insulAtTab = double.IsNaN(g.TabInsulThickMm) ? 0 : g.TabInsulThickMm;
                    double jStab = LocalStability.Check(p2, tAtJ, jT, thickAtJ, insulAtTab, latMm).JStab;
                    return (th, jT, jD, mesh.VolumeMm3 * Materials.PtDensity * 1e-6, jStab);
                }

                static string SgnE9(double v, string f = "0") => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString(f);

                void HeadE9() => Console.WriteLine(
                    $"{"配置",-24}{"I A",6}{"盘发热",8}{"盘散热",8}{"舌发热",8}{"舌散热",8}" +
                    $"{"铜排",7}{"抽管",8}{"残差",7}{"舌温",6}{"峰温",6}{"J舌",6}{"J稳",6}{"克",6}  判定");

                void RowE9(string nm, FlangePlate g, double iA, double clampC)
                {
                    ShellThermalResult th; double jT, jD, mg, jStab;
                    try { (th, jT, jD, mg, jStab) = SolveOneE9(g, iA, clampC); }
                    catch (Exception ex) { Console.WriteLine($"{nm,-24}  异常 {ex.Message}"); return; }
                    // ★ 先判物性：越过熔点的「解」根本不存在，能量账闭合得再好也没用。
                    //   拟合到 3392 °C 才反号 ⇒ 求解器不会自己拒绝，必须在这里拦。
                    string verdict;
                    if (th.OverMelt) verdict = $"✗✗ 已熔（>{Materials.PtMeltC:0}）";
                    else
                    {
                        // C2 是 |管根温差| ≤ 10 K，对应「抽管」要落在 0 附近的一条窄带里，
                        // 不是越负越好 —— 负得太多是倒灌，管根变热点（§4.3c 的 HC2 −88 K）。
                        verdict = Math.Abs(th.QFromTubeW) < 15 ? "✓ 近平衡"
                                : th.QFromTubeW >= 15 ? "✗ 抽管"
                                : "✗ 倒灌";
                        if (!double.IsNaN(jStab) && jT > jStab) verdict += "／局部失稳";
                        if (th.OverFitRange) verdict += "／⚠外推";
                    }
                    Console.WriteLine(
                        $"{nm,-24}{iA,6:0}{th.QGenDiscW,8:0}{th.QLossDiscW,8:0}" +
                        $"{th.QGenTabW,8:0}{th.QLossTabW,8:0}{th.QToClampW,7:0}" +
                        $"{SgnE9(th.QFromTubeW),8}{SgnE9(th.EnergyResidualW, "0.0"),7}" +
                        $"{th.TTabMeanC,6:0}{th.TMaxC,6:0}{jT,6:0.0}{(double.IsNaN(jStab) ? -1 : jStab),6:0.0}" +
                        $"{mg,6:0}  {verdict}");
                }

                FlangePlate MkE9(double disc, double tabL, double halfW, double tTab,
                                 double tDisc, double tabInsul = double.NaN) => new()
                {
                    DiscRadiusMm = disc, HoleRadiusMm = holeE9,
                    TabEndXMm = -tabL, TabEndHalfWidthMm = halfW,
                    ThicknessMm = tDisc, ThickenedMm = tDisc, TabThicknessMm = tTab,
                    InsulBoundaryXMm = double.NaN, TabInsulThickMm = tabInsul
                };

                Console.WriteLine("=== 端片可行性图：分区能量账 ===");
                Console.WriteLine($"定管根 {tRootE9:0} °C；圆盘包 20 mm；压接 40 mm；单位 W");
                Console.WriteLine("闭合式（自由单元）：(发热−散热)盘 + (发热−散热)舌 + 抽管 − 铜排 = 残差 ≈ 0");
                Console.WriteLine("★「抽管」是目标列：>0 法兰抽管子的热（管根塌），<0 法兰倒灌（管根成热点）。");
                Console.WriteLine("  C2 要的是 |管根温差| ≤10 K ⇒ **这一列要落在 0 附近，不是越负越好**。");
                Console.WriteLine("「J稳」= 舌片 J 峰值那一点的局部失稳上限（LocalStability，按该点温度与厚度实算）。");
                Console.WriteLine();

                Console.WriteLine("── ① 基线复现（对齐 §4.3a / §4.3d）");
                Console.WriteLine("   注：本命令把管根**钉在控温点**，而整线解里管根已被抽冷 ⇒ 这里的散热与抽热偏大，");
                Console.WriteLine("   是「管根不许塌」前提下的必要条件，比整线解严格。");
                HeadE9();
                RowE9("§4.3a 端片 夹300", MkE9(36, 90, 30, 0.8, 0.4), iEndE9, 300);
                RowE9("§4.3a 共用 夹300", MkE9(36, 90, 30, 0.8, 0.4), iShareE9, 300);
                RowE9("§4.3c 端片 自由端", MkE9(36, 90, 30, 0.8, 0.4), iEndE9, -1);
                RowE9("§4.3d 端片 舌50自由", MkE9(30, 50, 30, 0.8, 0.4), iEndE9, -1);
                Console.WriteLine();

                Console.WriteLine("── ② 舌片末端宽度（末端截面固定 27.5 mm²；注意舌片是**梯形**，");
                Console.WriteLine("   自切点的半宽约 29 mm 收到末端半宽 ⇒ 末端收窄并不能把整条舌都变窄）");
                HeadE9();
                foreach (double fw in new[] { 60.0, 40.0, 30.0, 20.0, 16.0, 12.0 })
                    RowE9($"舌90末{fw:0}×{27.5 / fw:0.00} 自由", MkE9(30, 90, fw / 2, 27.5 / fw, 0.4), iEndE9, -1);
                Console.WriteLine();

                Console.WriteLine("── ③ 舌片长度（压接 40 mm 是定长 ⇒ 短舌几乎整条被压接吃掉）");
                HeadE9();
                foreach (double tl in new[] { 50.0, 70.0, 90.0, 130.0, 180.0 })
                    RowE9($"舌{tl:0}末20×1.38 自由", MkE9(30, tl, 10, 1.375, 0.4), iEndE9, -1);
                Console.WriteLine();

                Console.WriteLine("── ④ 圆盘半径（盘的**远侧**是电流死区：只散热不发热）");
                HeadE9();
                foreach (double dr in new[] { 40.0, 36.0, 32.0, 30.0, 28.0 })
                    RowE9($"盘Ø{2 * dr:0} 舌90末20×1.38", MkE9(dr, 90, 10, 1.375, 0.4), iEndE9, -1);
                Console.WriteLine();

                Console.WriteLine("── ⑤ 电流（= 管壁杠杆：I ∝ √壁厚 ⇒ 发热 ∝ 壁厚，代价是管子的铂重）");
                HeadE9();
                foreach (double ia in new[] { 500.0, 687.0, 900.0, 1105.0, 1400.0 })
                    RowE9($"I={ia:0} 舌90末20×1.38", MkE9(30, 90, 10, 1.375, 0.4), ia, -1);
                Console.WriteLine();

                Console.WriteLine("── ⑥ ★ 舌片保温厚度：**新自由度**（总纲④，此前只有裸/全包两档）");
                Console.WriteLine("   裸露时净抽热、全包时净倒灌 ⇒ 中间必有零点。找那个厚度。");
                HeadE9();
                foreach (double ti in new[] { double.NaN, 0.5, 1.0, 2.0, 3.0, 5.0, 8.0, 20.0 })
                    RowE9($"舌90末60×0.46 包{(double.IsNaN(ti) ? "裸" : ti.ToString("0.0"))}",
                          MkE9(30, 90, 30, 0.46, 0.4, ti), iEndE9, -1);
                Console.WriteLine();
                foreach (double ti in new[] { double.NaN, 1.0, 2.0, 3.0, 5.0, 8.0, 20.0 })
                    RowE9($"舌90末20×1.38 包{(double.IsNaN(ti) ? "裸" : ti.ToString("0.0"))}",
                          MkE9(30, 90, 10, 1.375, 0.4, ti), iEndE9, -1);
                Console.WriteLine();

                Console.WriteLine("── ⑦ 共用片（I=1105）在同一张图上：它的零点必然在**更薄的保温**上");
                HeadE9();
                foreach (double ti in new[] { double.NaN, 0.5, 1.0, 2.0, 3.0 })
                    RowE9($"共用 舌90末60×0.46 包{(double.IsNaN(ti) ? "裸" : ti.ToString("0.0"))}",
                          MkE9(30, 90, 30, 0.46, 0.4, ti), iShareE9, -1);
                Console.WriteLine();

                // ── ⑧ 两个旋钮对两条约束：舌厚调稳定性、舌保温调热平衡
                //
                // 从 ②–⑦ 读出来的机理（都是实算，不是推的）：
                //   · 自给（抽管→0）由**裸露散热**定 ⇒ 旋钮是舌片保温厚度，且它穿过 0；
                //   · 局部稳定 J_stab = √[(2dq″/dT + k·t/L²)/(ρe·t·TCR)]，实测 ∝ 1/√t
                //     （末60×0.46 的 J稳 44.9 vs 末20×1.38 的 23.1，厚度 3× ⇒ 比 1.94 ≈ √3）
                //     而 J ∝ 1/t ⇒ **裕度 J_stab/J ∝ √t**，旋钮是舌片厚度。
                // 两个旋钮各自主导一条约束 ⇒ 这是个 2×2 方程组，不是一维死路。
                Console.WriteLine("── ⑧ ★★ 二维：舌片厚度 × 舌片保温（两个旋钮对两条约束）");
                Console.WriteLine("   厚度调稳定裕度（J_stab/J ∝ √t），保温调热平衡（抽管穿零）。找同时过的格。");
                HeadE9();
                foreach (double tt in new[] { 0.8, 1.2, 1.6, 2.2, 3.0 })
                {
                    foreach (double ti in new[] { 2.0, 4.0, 6.0, 9.0, 14.0 })
                        RowE9($"末20×{tt:0.0} 包{ti:0}", MkE9(30, 90, 10, tt, 0.4, ti), iEndE9, -1);
                    Console.WriteLine();
                }

                Console.WriteLine("★ 读法：⑥⑦ 若「抽管」随保温厚度**穿过 0**，则端片不再是死路 ——");
                Console.WriteLine("  「端片能不能自给」从是非题变成解方程，且这个自由度**不花铂**（只是纤维）。");
                Console.WriteLine("  ⑧ 找「✓ 近平衡」且不带「局部失稳」「外推」「已熔」的格 —— 那才是候选。");
                Console.WriteLine("  ⚠ 本节全部用**自由端**（铜排不导热），那是物理上不成立的极限，");
                Console.WriteLine("     真实接头温度须用 --busbar2 的热导边界重算。");
                Console.WriteLine("  本命令是**单片定管根**的必要条件筛，候选须走 LineRunner 整线复核。");
                return;
            }

            // --cli --linefinal   ★★★★ 整线自洽复核：把 --endsolve 的候选放进 LineRunner
            //
            // 为什么必须再跑一遍整线：--endsolve 是**单片、定管根、定电流**的筛子，
            // 其中电流 687/1105 是按**壁厚 0.4** 算出来的旧数。改成焊接下界 0.6 后
            // I ∝ √壁厚 ⇒ 电流涨约 22 %、法兰发热涨约 50 %，单片筛的结论会整体漂移。
            // 只有 LineRunner 才同时解：逐段电流、段↔法兰耦合、管根温差 C2、升温规程。
            //
            // 外层再套一个**逐片调舌片保温**的定点迭代：保温是不花铂的连续旋钮（§4.3e），
            // 让它去顶 C2，铂重就只由(舌长,末宽,舌厚)决定 —— 这正是想要的解耦。
            if (args.Contains("--linefinal"))
            {
                double wallF9 = p.WeldMinThicknessMm;      // 焊接下界（用户 2026-08-14）
                double discF9 = 30.0, holeF9 = wallF9 + 25.0;

                // ★★ 盘与舌**等厚**（`--uniform`，默认开）。
                //
                // 理由是可制造性：舌片与圆盘是**同一张板切出来的**（所以它们之间没有焊缝，
                // §4.3f 正是据此把舌厚从焊接下界里解放出来）—— 既然同板，就不可能一个 0.5
                // 一个 2.02，那需要机加工或拼板另焊。
                // 而且实测（`--hotspot`）表明阶梯本身就是祸根：
                //   J 峰值 62.9 落在**贴着管孔的 0.5 mm 圆盘上**（r=26.3），
                //   T 峰值落在**贴着管孔的舌片根部**（厚 2.02）——
                //   电流必须从管孔挤向舌片，而那段颈正好是全片最薄处。
                // 等厚把这个颈的截面直接放大 3–4 倍。代价是圆盘变重（约 +25 g/片）。
                //
                // 传 `--stepped` 回到阶梯板（可比较两者的铂重差）。
                bool uniformF9 = !args.Contains("--stepped");
                double clampLenF9 = 40.0, clampF9 = 300.0;
                double targetF9 = 5.0;                     // 管根温差目标 K（C2 上限 10）

                var pF9 = SegmentSolver.Clone(p);
                pF9.Layer1.ThicknessMm = 10.0; pF9.Layer1.Enabled = true;
                pF9.WallMinMm = wallF9;
                pF9.FlangeInsulThickMm = 20; pF9.FlangeInsulated = true;
                pF9.BusbarClampLengthMm = clampLenF9;
                pF9.BusbarClampTempC = clampF9;

                // --endsolve 选出的最轻可行候选（端片 / 共用片）
                // 起点 = 上一次自动定尺寸的收敛解（2026-08-14 实算），这样重跑只需几轮。
                // 想从 --endsolve 的单片候选重新走一遍，把 ins/tTab 换回 1.9/25.7、0.9/1.8 即可。
                var geoF9 = new[]
                {
                    (nm: "入口端片",  tabL: 90.0,  halfW: 15.0, tTab: 1.37, ins: 16.5),
                    (nm: "HC1|HC2", tabL: 90.0,  halfW: 15.0, tTab: 2.02, ins: 0.9),
                    (nm: "HC2|HC3", tabL: 90.0,  halfW: 15.0, tTab: 1.80, ins: 1.1),
                    (nm: "出口端片",  tabL: 90.0,  halfW: 15.0, tTab: 1.04, ins: 2.9),
                };
                var insF9 = geoF9.Select(g => g.ins).ToArray();
                var tabF9 = geoF9.Select(g => g.tTab).ToArray();
                const double insLoF9 = 0.5, insHiF9 = 30.0;

                LineCase MakeF9(double[] ins, double[] tab)
                {
                    var plates = new FlangePlate[4];
                    for (int j = 0; j < 4; j++)
                    {
                        // 等厚：圆盘随舌片走（但不得低于焊接下界）；阶梯：圆盘钉在下界
                        double tDisc = uniformF9 ? Math.Max(tab[j], p.WeldMinThicknessMm) : 0.5;
                        plates[j] = new FlangePlate
                        {
                            DiscRadiusMm = discF9, HoleRadiusMm = holeF9,
                            TabEndXMm = -geoF9[j].tabL, TabEndHalfWidthMm = geoF9[j].halfW,
                            ThicknessMm = tDisc, ThickenedMm = tDisc,
                            TabThicknessMm = uniformF9 ? double.NaN : tab[j],
                            InsulBoundaryXMm = double.NaN, TabInsulThickMm = ins[j],
                            TabParallel = true,
                            // 等宽舌片与圆盘的交界必须倒角：尖角同时抬高该处温度与 J，
                            // 且尖角处的场是网格相关的奇异解（见 FlangePlate.TabFilletMm）。
                            TabFilletMm = 3.0,
                            // 角焊缝：焊脚取较薄件的厚度（常规做法），管↔盘两面各一道。
                            // 它在孔周增厚 ⇒ 压低该处 J 与单位面积发热（用户 2026-08-14 附图）。
                            WeldFilletLegMm = Math.Max(tDisc, wallF9)
                        };
                    }
                    return new LineCase
                    {
                        Base = SegmentSolver.Clone(pF9), WallMm = wallF9,
                        UseMeasuredCurrent = false, CheckRamp = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = plates,
                        ClampTempC = new[] { clampF9, clampF9, clampF9, clampF9 }
                    };
                }

                Console.WriteLine("=== 整线自洽复核（--endsolve 候选 → LineRunner）===");
                Console.WriteLine($"管壁 {wallF9:0.0}（焊接下界）／盘Ø{2 * discF9:0}／等宽舌片／" +
                                  $"压接 {clampLenF9:0} 夹 {clampF9:0} °C／圆盘包 20");
                Console.WriteLine(uniformF9
                    ? "★ **盘舌等厚**（同板切出，唯一可制造的形式）；传 --stepped 可看阶梯板对比"
                    : "★ 阶梯板：盘钉在焊接下界 0.5、舌片单独设厚 ⇒ 需机加工或拼板另焊");
                Console.WriteLine("外层**双旋钮**定点迭代顶管根温差 C2：");
                Console.WriteLine("  · 首选**舌片保温厚度** —— 不花铂，所以先用它");
                Console.WriteLine("  · 保温顶到边界还不够时，才动**舌片厚度**（发热 ∝ 1/t，这一项要花铂）");
                Console.WriteLine();
                Console.WriteLine($"{"轮",4}{"保温 入/共1/共2/出",22}{"舌厚 入/共1/共2/出",22}" +
                                  $"{"HC1 ΔT",9}{"HC2 ΔT",9}{"HC3 ΔT",9}  状态");

                LineResult? last = null;
                for (int round = 0; round < 24; round++)
                {
                    LineResult rF9;
                    try { rF9 = LineRunner.Run(MakeF9(insF9, tabF9)); }
                    catch (Exception ex) { Console.WriteLine($"{round,4}  异常 {ex.Message}"); break; }
                    if (!rF9.Ok) { Console.WriteLine($"{round,4}  ✗ {rF9.Message}"); break; }
                    last = rF9;

                    var dt = rF9.Segments.Select(s => s.RootDeltaK).ToArray();
                    Console.WriteLine($"{round,4}" +
                        $"{string.Join("/", insF9.Select(v => v.ToString("0.0"))),22}" +
                        $"{string.Join("/", tabF9.Select(v => v.ToString("0.00"))),22}" +
                        $"{dt[0],9:+0.0;−0.0}{dt[1],9:+0.0;−0.0}{dt[2],9:+0.0;−0.0}  " +
                        (rF9.Converged ? "耦合✓" : "耦合⚠"));

                    if (dt.All(d => d > 0 && d <= 10.0)) { Console.WriteLine("   ⇒ C2 全过，停"); break; }

                    // 片 j 影响段 j−1 与 j。误差 e>0 = 管根偏冷 = 法兰在抽热 ⇒ 要**减**法兰的净吸热。
                    var nIns = (double[])insF9.Clone();
                    var nTab = (double[])tabF9.Clone();
                    bool moved = false;
                    for (int j = 0; j < 4; j++)
                    {
                        double e = 0; int c = 0;
                        if (j - 1 >= 0 && j - 1 < dt.Length) { e += dt[j - 1] - targetF9; c++; }
                        if (j < dt.Length) { e += dt[j] - targetF9; c++; }
                        if (c == 0) continue;
                        e /= c;
                        if (Math.Abs(e) < 1.0) continue;

                        // ① 先动保温（不花铂）：抽热 ⇒ 加保温；倒灌 ⇒ 减保温
                        double want = Math.Clamp(insF9[j] + 0.06 * e, insLoF9, insHiF9);
                        if (Math.Abs(want - insF9[j]) > 1e-9) { nIns[j] = want; moved = true; continue; }

                        // ② 保温已顶到边界仍不够 ⇒ 动舌厚。发热 ∝ 1/t：
                        //    倒灌(e<0, 发热过多) ⇒ 加厚；抽热(e>0, 发热不足) ⇒ 减薄。
                        double step = Math.Clamp(0.004 * Math.Abs(e), 0.01, 0.15);
                        double t2 = e < 0 ? tabF9[j] * (1 + step) : tabF9[j] * (1 - step);
                        nTab[j] = Math.Clamp(t2, 0.3, 4.0);
                        if (Math.Abs(nTab[j] - tabF9[j]) > 1e-9) moved = true;
                    }
                    if (!moved) { Console.WriteLine("   ⇒ 两个旋钮都到位或都顶死，停"); break; }
                    insF9 = nIns; tabF9 = nTab;
                }

                // ★ 收敛后必须再跑一次**带全部判据**的（迭代期关掉升温判据只是为了快）。
                //   用户的硬约束①是「能达成升温功能且升温途中不损坏」，而现场唯一被证实的
                //   失效模式正在升温期（§4.2r）—— 稳态过了不等于能开机。
                if (last != null)
                {
                    Console.WriteLine();
                    Console.WriteLine("── 收敛后全判据复核（含升温规程）");
                    var lcChk = MakeF9(insF9, tabF9);
                    lcChk.CheckRamp = true;
                    try
                    {
                        var rChk = LineRunner.Run(lcChk);
                        if (rChk.Ok)
                        {
                            last = rChk;
                            Console.WriteLine($"{"判据",-24}{"实际",10}{"限值",10}{"位置",10}  结论");
                            foreach (var ck in rChk.Checks)
                                Console.WriteLine($"{ck.Name,-24}{ck.Actual,10:0.00}{ck.Limit,10:0.00}" +
                                    $"{ck.Where,10}  {(ck.Ok ? "✓" : "✗")}　{ck.Note}");
                        }
                        else Console.WriteLine("  ✗ " + rChk.Message);
                    }
                    catch (Exception ex) { Console.WriteLine("  异常 " + ex.Message); }
                }

                if (last != null)
                {
                    Console.WriteLine();
                    Console.WriteLine("── 逐片明细");
                    Console.WriteLine($"{"片",10}{"电流A",8}{"舌厚",7}{"舌保温",8}{"盘净W",8}{"舌净W",8}" +
                                      $"{"铜排W",8}{"抽管W",8}{"残差",7}{"峰温",7}{"克",7}");
                    for (int j = 0; j < last.Flanges.Length; j++)
                    {
                        var f = last.Flanges[j];
                        Console.WriteLine($"{f.Name,10}{f.CurrentA,8:0}{tabF9[j],7:0.00}{insF9[j],8:0.0}" +
                            $"{f.QGenDiscW - f.QLossDiscW,8:+0;−0}{f.QGenTabW - f.QLossTabW,8:+0;−0}" +
                            $"{f.QClampW,8:0}{f.QFromTubeW,8:+0;−0}{f.EnergyResidualW,7:+0.0;−0.0}" +
                            $"{f.TMaxC,7:0}{f.MassG,7:0}");
                    }
                    Console.WriteLine();
                    Console.WriteLine($"{"段",10}{"控温°C",9}{"电流A",8}{"管根ΔT K",11}{"管 J",8}{"克",8}");
                    foreach (var s in last.Segments)
                        Console.WriteLine($"{s.Name,10}{s.SetpointC,9:0}{s.CurrentA,8:0}" +
                            $"{s.RootDeltaK,11:+0.0;−0.0}{s.TubeJAPerMm2,8:0.00}{s.MassG,8:0}");
                    Console.WriteLine();
                    double mFl = last.Flanges.Sum(f => f.MassG), mSeg = last.Segments.Sum(s => s.MassG);
                    Console.WriteLine($"铂重：管 {mSeg:0} + 法兰 {mFl:0} = **{mSeg + mFl:0} g**" +
                                      $"　（现状基准 {last.BaselineMassG:0} g ⇒ 省 " +
                                      $"{(1 - (mSeg + mFl) / last.BaselineMassG) * 100:0.0} %）");
                    foreach (var n in last.Notes) Console.WriteLine("  " + n);
                }
                return;
            }

            // --cli --weldvalue   ★★★★ 「换一种焊接方法值多少铂」——用户 2026-08-14 答「目前手工焊接」
            //
            // 为什么这条现在最值钱：定案方案 2204 g 里**管子占 1841 g（84 %）**，
            // 而管壁的下界**唯一**由焊接方法定（--weldmin ③：管侧圆筒不会屈曲，只可能烧穿）。
            // 圆盘那一侧反而不受影响 —— 它的屈曲下界 0.55 mm 已被电热约束（1.0–2.0 mm）盖住。
            // ⇒ 整条「焊接方法 → 省铂」的传导路径只有一条：**管壁**。
            //
            // 不能只按 m ∝ 壁厚 换算，因为薄壁会同时动三样：
            //   ① I ∝ √壁厚 ⇒ 法兰发热 ∝ I² ∝ 壁厚 ⇒ 法兰要跟着重新定尺寸（否则倒灌或抽热）
            //   ② 管 J ∝ 1/√壁厚 ⇒ 会去顶热稳定极限
            //   ③ 可用功率 ∝ 截面 ⇒ 升温时间变长，可能顶穿「≤3 天」
            // 所以每一档都得把整线重解一遍。
            if (args.Contains("--weldvalue"))
            {
                double discW9 = 30.0, clampLenW9 = 40.0, clampW9 = 300.0, targetW9 = 5.0;

                // 各档焊接方法对应的管壁烧穿下界（--weldmin ③；行业常规值，非本项目实测）
                var methods = new[]
                {
                    (nm: "手工 TIG（保守）", wall: 0.60),
                    (nm: "手工 TIG",        wall: 0.50),
                    (nm: "自动 TIG",        wall: 0.30),
                    (nm: "激光/电阻缝焊",    wall: 0.15),
                };

                Console.WriteLine("=== 焊接方法值多少铂 ===");
                Console.WriteLine("用户 2026-08-14：**目前是手工焊接** ⇒ 当前档位是第 1–2 行。");
                Console.WriteLine($"盘Ø{2 * discW9:0}／等宽舌片／盘舌等厚／压接 {clampLenW9:0} 夹 {clampW9:0} °C／圆盘包 20");
                Console.WriteLine("每一档都重新跑「保温+舌厚」双旋钮定点迭代顶 C2，再全判据复核。");
                Console.WriteLine();

                // 起点 = 0.6 档的收敛解（--linefinal 实算）；逐档按 t_tab ∝ 壁厚 外推
                double[] insW9 = { 16.5, 0.9, 1.1, 2.9 };
                double[] tabW9 = { 1.37, 2.02, 1.80, 1.04 };
                double[] halfW9 = { 15.0, 15.0, 15.0, 15.0 };
                double[] tabLW9 = { 90.0, 90.0, 90.0, 90.0 };
                const double insLoW9 = 0.5, insHiW9 = 30.0;
                double prevWall = 0.60;

                var rows = new List<(string nm, double wall, double mSeg, double mFl,
                                     double dtMin, double dtMax, double tubeJ, bool ok, string why)>();

                foreach (var meth in methods)
                {
                    double wallW9 = meth.wall, holeW9 = wallW9 + 25.0;

                    // 法兰发热 ∝ I² ∝ 壁厚，而发热 ∝ 1/t_tab ⇒ 等发热要求 t_tab ∝ 壁厚
                    double sc = wallW9 / prevWall;
                    for (int j = 0; j < 4; j++) tabW9[j] = Math.Clamp(tabW9[j] * sc, 0.3, 4.0);
                    prevWall = wallW9;

                    var pW9 = SegmentSolver.Clone(p);
                    pW9.Layer1.ThicknessMm = 10.0; pW9.Layer1.Enabled = true;
                    pW9.WallMinMm = wallW9;
                    pW9.FlangeInsulThickMm = 20; pW9.FlangeInsulated = true;
                    pW9.BusbarClampLengthMm = clampLenW9;
                    pW9.BusbarClampTempC = clampW9;

                    // 圆盘厚度：等厚随舌片走，但不得低于**屈曲**下界（与焊接方法无关，
                    // 只随环宽变；Ø60 环宽 4 mm ⇒ 0.0693×4×2 = 0.55）。
                    double discFloor = WeldDistortion.ForPt(1.0, kb: 0.43).SlopePerB
                                       * (discW9 - 26.0) * p.WeldSafetyFactor;

                    LineCase MakeW9(double[] ins, double[] tab)
                    {
                        var plates = new FlangePlate[4];
                        for (int j = 0; j < 4; j++)
                        {
                            double tDisc = Math.Max(tab[j], discFloor);
                            plates[j] = new FlangePlate
                            {
                                DiscRadiusMm = discW9, HoleRadiusMm = holeW9,
                                TabEndXMm = -tabLW9[j], TabEndHalfWidthMm = halfW9[j],
                                ThicknessMm = tDisc, ThickenedMm = tDisc,
                                TabThicknessMm = double.NaN,       // 等厚（同板切出）
                                InsulBoundaryXMm = double.NaN, TabInsulThickMm = ins[j],
                                TabParallel = true, TabFilletMm = 3.0,
                                WeldFilletLegMm = Math.Max(tDisc, wallW9)
                            };
                        }
                        return new LineCase
                        {
                            Base = SegmentSolver.Clone(pW9), WallMm = wallW9,
                            UseMeasuredCurrent = false, CheckRamp = false,
                            SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                            FlangePlates = plates,
                            ClampTempC = new[] { clampW9, clampW9, clampW9, clampW9 }
                        };
                    }

                    Console.WriteLine($"── {meth.nm}：管壁 {wallW9:0.00} mm　（圆盘屈曲下界 {discFloor:0.00}）");

                    LineResult? lastW9 = null;
                    for (int round = 0; round < 18; round++)
                    {
                        LineResult rW9;
                        try { rW9 = LineRunner.Run(MakeW9(insW9, tabW9)); }
                        catch (Exception ex) { Console.WriteLine($"   异常 {ex.Message}"); break; }
                        if (!rW9.Ok) { Console.WriteLine($"   ✗ {rW9.Message}"); break; }
                        lastW9 = rW9;

                        var dt = rW9.Segments.Select(s => s.RootDeltaK).ToArray();
                        if (dt.All(d => d > 0 && d <= 10.0)) break;

                        var nIns = (double[])insW9.Clone();
                        var nTab = (double[])tabW9.Clone();
                        bool moved = false;
                        for (int j = 0; j < 4; j++)
                        {
                            double e = 0; int c = 0;
                            if (j - 1 >= 0 && j - 1 < dt.Length) { e += dt[j - 1] - targetW9; c++; }
                            if (j < dt.Length) { e += dt[j] - targetW9; c++; }
                            if (c == 0) continue;
                            e /= c;
                            if (Math.Abs(e) < 1.0) continue;

                            double want = Math.Clamp(insW9[j] + 0.06 * e, insLoW9, insHiW9);
                            if (Math.Abs(want - insW9[j]) > 1e-9) { nIns[j] = want; moved = true; continue; }

                            double step = Math.Clamp(0.004 * Math.Abs(e), 0.01, 0.15);
                            double t2 = e < 0 ? tabW9[j] * (1 + step) : tabW9[j] * (1 - step);
                            nTab[j] = Math.Clamp(t2, 0.3, 4.0);
                            if (Math.Abs(nTab[j] - tabW9[j]) > 1e-9) moved = true;
                        }
                        if (!moved) break;
                        insW9 = nIns; tabW9 = nTab;
                    }

                    if (lastW9 == null) { Console.WriteLine("   ✗ 无解，跳过"); Console.WriteLine(); continue; }

                    // 全判据复核（含升温规程）——薄壁最可能栽在这一步
                    var lcW9 = MakeW9(insW9, tabW9); lcW9.CheckRamp = true;
                    string why = "";
                    bool allOk = true;
                    try
                    {
                        var rc = LineRunner.Run(lcW9);
                        if (rc.Ok)
                        {
                            lastW9 = rc;
                            foreach (var ck in rc.Checks.Where(x => !x.Ok))
                            { allOk = false; why += (why.Length > 0 ? "；" : "") + $"{ck.Name} {ck.Actual:0.00}>{ck.Limit:0.00}"; }
                        }
                        else { allOk = false; why = rc.Message; }
                    }
                    catch (Exception ex) { allOk = false; why = ex.Message; }

                    var dtF = lastW9.Segments.Select(s => s.RootDeltaK).ToArray();
                    if (!(dtF.All(d => d > 0 && d <= 10.0))) { allOk = false; why = (why.Length > 0 ? why + "；" : "") + "C2 未过"; }

                    double mSegW = lastW9.Segments.Sum(s => s.MassG);
                    double mFlW = lastW9.Flanges.Sum(f => f.MassG);
                    double tubeJW = lastW9.Segments.Max(s => s.TubeJAPerMm2);

                    Console.WriteLine($"   舌厚 {string.Join("/", tabW9.Select(v => v.ToString("0.00")))}" +
                                      $"　保温 {string.Join("/", insW9.Select(v => v.ToString("0.0")))}");
                    Console.WriteLine($"   管根ΔT {string.Join(" / ", dtF.Select(v => v.ToString("+0.0;−0.0")))} K" +
                                      $"　管 J_max {tubeJW:0.00}　管 {mSegW:0} + 法兰 {mFlW:0} = **{mSegW + mFlW:0} g**" +
                                      (allOk ? "　✓" : $"　✗ {why}"));
                    Console.WriteLine();

                    rows.Add((meth.nm, wallW9, mSegW, mFlW, dtF.Min(), dtF.Max(), tubeJW, allOk, why));
                }

                Console.WriteLine("── 汇总：焊接方法 → 整线铂重");
                Console.WriteLine($"{"焊接方法",-18}{"管壁",7}{"管 g",8}{"法兰 g",8}{"合计 g",9}{"省 %",8}{"管J",7}  判据");
                double baseM = 7141.0;
                foreach (var rw in rows)
                    Console.WriteLine($"{rw.nm,-18}{rw.wall,7:0.00}{rw.mSeg,8:0}{rw.mFl,8:0}{rw.mSeg + rw.mFl,9:0}" +
                        $"{(1 - (rw.mSeg + rw.mFl) / baseM) * 100,8:0.0}{rw.tubeJ,7:0.00}  " +
                        (rw.ok ? "✓ 全过" : "✗ " + rw.why));
                Console.WriteLine();
                Console.WriteLine("★ 读法：只有标 ✓ 的行可交付。管 J 顶到热稳定极限、或升温超 3 天，");
                Console.WriteLine("  都会在这里现形 —— 那时再薄的焊接能力也换不成铂。");
                return;
            }

            // --cli --taper   ★★★★ 加厚形状的取舍：压平尖峰 vs 保住发热
            //
            // 到这一步，② 的残余（+1.9）与 C2 的余量（HC3 +9.5，上限 10）**同时见底**，
            // 而 --clampscan 证明压接温度只会让两者一起变坏。剩下的只有加厚的**形状**。
            //
            // 关键权衡此前一直没被量出来：单片筛只报了 T 峰值，**没报发热**。
            // 于是「R=33 比 R=30 好」看着成立，实算却把 C2 从 +24 炸到 +66 ——
            // 因为多伸的那 3 mm 正好落在舌片高 J 区，按 K²/t 把发热砍掉一大块。
            // ⇒ 本条**同时报 T 峰值与整片发热**，让取舍看得见：
            //    要的是「峰值 ≤ 管根」且「发热尽可能大」的那一个。
            //
            // 台阶形状用 DiscStepRadiiMm/DiscStepThicknessMm（按半径分级，等厚板上
            // 对圆盘与舌片根部同时生效），可表达单级环、两级渐变环。
            if (args.Contains("--taper"))
            {
                double wallT = p.WeldMinThicknessMm, discT = 30.0, clampLenT = 40.0;
                var pT = SegmentSolver.Clone(p);
                pT.Layer1.ThicknessMm = 10.0; pT.Layer1.Enabled = true;
                pT.WallMinMm = wallT;
                pT.FlangeInsulThickMm = 20; pT.FlangeInsulated = true;
                pT.BusbarClampLengthMm = clampLenT; pT.BusbarClampTempC = 300;

                double holeT = wallT + 25.0;
                // ⚠ 此前只拿**入口片**当靶（I=843），可整线上 ② 最差的是 **HC2|HC3（I=1201）**——
                //   给错的片做优化，白做。台阶强度必须**逐片**定：局部发热 ∝ J²∝I²，
                //   共用片走 √3 倍电流，尖峰强度是端片的约 2.6 倍。
                //   下面四片各扫各的（电流与管根温度取 --final2 定案档 450 °C 的收敛值）。
                var targetsT = new[]
                {
                    (nm: "入口端片",  I: 843.0,  tRoot: 1143.1, tBase: 1.37, ins: 18.2),
                    (nm: "HC1|HC2", I: 1354.0, tRoot: 1143.1, tBase: 2.08, ins: 0.8),
                    (nm: "HC2|HC3", I: 1201.0, tRoot: 1078.6, tBase: 1.86, ins: 0.9),
                    (nm: "出口端片",  I: 669.0,  tRoot: 1045.0, tBase: 1.04, ins: 7.5),
                };
                double tBaseT = targetsT[0].tBase, insT = targetsT[0].ins;
                double iT2 = targetsT[0].I, tRootT = targetsT[0].tRoot;

                Console.WriteLine("=== 加厚形状：压平尖峰 vs 保住发热 ===");
                Console.WriteLine($"靶：入口片　I={iT2:0} A　管根 {tRootT:0.0} °C　基板 {tBaseT:0.00}　舌保温 {insT:0.0}");
                Console.WriteLine("★ 同时报**发热** —— 只压峰值不看发热，就是 R=33 那次把 C2 炸掉的原因。");
                Console.WriteLine();
                Console.WriteLine($"{"形状",-26}{"T峰−管根 K",13}{"发热 W",10}{"J峰",8}{"增重 g/片",11}");

                (double peak, double gen, double dm) Row(string nm, double[] radii, double[] th)
                {
                    var g = new FlangePlate
                    {
                        DiscRadiusMm = discT, HoleRadiusMm = holeT,
                        TabEndXMm = -90.0, TabEndHalfWidthMm = 15.0,
                        ThicknessMm = tBaseT, TabThicknessMm = double.NaN,
                        DiscStepRadiiMm = radii, DiscStepThicknessMm = th,
                        InsulBoundaryXMm = double.NaN, TabInsulThickMm = insT,
                        TabParallel = true, TabFilletMm = 3.0,
                        WeldFilletLegMm = Math.Max(tBaseT, wallT)
                    };
                    ShellMesh m;
                    try { m = FlangeMesher.Build(g, 0, 2.0, 11.0, 45.0, clampLenT); }
                    catch (Exception ex) { Console.WriteLine($"{nm,-26}网格失败 {ex.Message}"); return (1e9, -1, 0); }
                    var sc = ShellCurrent.Solve(m, iT2, Materials.PtResistivity(tRootT) * 1e3, tRootT);
                    var th2 = ShellThermal.Solve(m, sc.JMagAPerMm2, pT, tRootT,
                                  g.InsulBoundaryXResolved, tabBoundaryX: g.Tangent().X,
                                  tabInsulThickMm: g.TabInsulThickMm);
                    // 增重：逐格 (实际厚 − 基板厚) × 面积
                    double dm = 0;
                    for (int i = 0; i < m.CellCount; i++)
                        dm += Math.Max(0, m.Thickness[i] - tBaseT) * m.Area[i];
                    Console.WriteLine($"{nm,-26}{th2.T.Max() - tRootT,13:+0.00;−0.00}{th2.QGenW,10:0}" +
                                      $"{sc.JMagAPerMm2.Max(),8:0.0}{dm * 1e-3 * 21.45,11:0.0}");
                    return (th2.T.Max() - tRootT, th2.QGenW, dm * 1e-3 * 21.45);
                }

                // ★ 用户 2026-08-14 确认工艺：**机加工台阶**（不是另焊加强环）。
                //   ⇒ 管孔那条唯一的焊缝不受影响，且厚度剖面是**自由函数** ——
                //     两级、三级、四级对机加工代价相同，没有理由只试我随手列的那几组。
                //   最优剖面的形状由「发热 = I·J·ρe」定：**在温度不越限的前提下让 J 尽量高**，
                //   即厚度只在需要的地方加、加到刚好，其余一律留薄。所以下面按
                //   「起始厚度 × 衰减快慢」张成一族剖面来扫，而不是拍几个特例。
                // 逐片扫同一族剖面，各自挑「峰值 ≤ 0 且发热最大」的那一组
                foreach (var tg in targetsT)
                {
                    Console.WriteLine();
                    Console.WriteLine($"── {tg.nm}　I={tg.I:0} A　管根 {tg.tRoot:0.0} °C　" +
                                      $"基板 {tg.tBase:0.00}　舌保温 {tg.ins:0.0}");
                    Console.WriteLine($"{"形状",-26}{"T峰−管根 K",13}{"发热 W",10}{"J峰",8}{"增重 g/片",11}");
                    tBaseT = tg.tBase; insT = tg.ins; iT2 = tg.I; tRootT = tg.tRoot;

                    Row("无加厚（基准）", Array.Empty<double>(), Array.Empty<double>());
                    string bestNm = ""; double bestGen = -1, bestPk = 0, bestDm = 0;
                    foreach (double t0 in new[] { 2.0, 2.4, 3.0, 3.6, 4.2, 5.0 })
                        foreach (double decay in new[] { 0.55, 0.70, 0.85 })
                        {
                            var rr2 = new[] { 29.0, 32.0, 35.0, 38.0 };
                            var tt = new double[4];
                            for (int q = 0; q < 4; q++)
                                tt[q] = Math.Max(tBaseT, t0 * Math.Pow(decay, q));
                            var res = Row($"四级 起{t0:0.0} 衰减{decay:0.00}", rr2, tt);
                            if (res.peak <= 0 && res.gen > bestGen)
                            { bestGen = res.gen; bestNm = $"起{t0:0.0} 衰减{decay:0.00}"; bestPk = res.peak; bestDm = res.dm; }
                        }
                    Console.WriteLine(bestGen < 0
                        ? "   ⇒ ✗ 本片无任何剖面能把峰值压到 ≤0"
                        : $"   ⇒ ★ 最优 {bestNm}：峰 {bestPk:+0.00;−0.00}　发热 {bestGen:0} W　+{bestDm:0.0} g");
                }
                Console.WriteLine();
                Console.WriteLine("★ 选「T峰−管根 ≤ 0 且发热最大」的那一行；发热掉得多的，C2 会在整线上还回来。");
                Console.WriteLine("⚠ 单片筛偏乐观约 0.8 K（§4.3l），只可用来排序，过/不过仍以整线为准。");
                return;
            }

            // --cli --uniform   ★★★★★ 换目标：整根管的**轴向温度均匀度**
            //
            // 用户 2026-08-14 更正了我一条错话：我说过渡段「没有玻璃、高几度低几度无所谓」——
            // **错**，管子从头到尾都走玻璃，过渡段当然也走。低几度就可能析晶。
            // 用户：「先不考虑析晶，仅量把温度做均匀即可」。
            //
            // ⇒ 评价指标从「管根温差 C2」换成**整线管温的 max−min**。
            //   C2 只判法兰那一个点，看不见全貌；而玻璃感受到的是整条剖面。
            //   剖面 (X/TMetal/TGlass) 一直在 SegmentOut 里，只是从没被打印过。
            //
            // 同时把**最低管温**一并报出来：将来要重新纳入析晶，直接拿它和液相线比即可。
            if (args.Contains("--uniform"))
            {
                double wallU = p.WeldMinThicknessMm, discU = 30.0;
                double tubeInsU = 5.0, clampU = 450.0, clampLenU = 40.0;   // combo 扫描的最优档
                double[] tabU = { 1.68, 2.71, 2.59, 1.39 }, insU = { 3.6, 0.5, 0.4, 3.3 };
                double[] stepRU = { 30.0, 36.0 }, stepTU = { 2.4, 1.7 };
                double discFloorU = WeldDistortion.ForPt(1.0, kb: 0.43).SlopePerB
                                    * (discU - 26.0) * p.WeldSafetyFactor;

                var pU = SegmentSolver.Clone(p);
                pU.Layer1.ThicknessMm = tubeInsU; pU.Layer1.Enabled = true;
                pU.WallMinMm = wallU;
                pU.FlangeInsulThickMm = 20; pU.FlangeInsulated = true;
                pU.BusbarClampLengthMm = clampLenU; pU.BusbarClampTempC = clampU;

                var platesU = new FlangePlate[4];
                for (int j = 0; j < 4; j++)
                {
                    double td = Math.Max(tabU[j], discFloorU);
                    platesU[j] = new FlangePlate
                    {
                        DiscRadiusMm = discU, HoleRadiusMm = wallU + 25.0,
                        TabEndXMm = -90.0, TabEndHalfWidthMm = 15.0,
                        ThicknessMm = td,
                        DiscStepRadiiMm = stepRU,
                        DiscStepThicknessMm = stepTU.Select(v => Math.Max(v, td)).ToArray(),
                        TabThicknessMm = double.NaN,
                        InsulBoundaryXMm = double.NaN, TabInsulThickMm = insU[j],
                        TabParallel = true, TabFilletMm = 3.0,
                        WeldFilletLegMm = Math.Max(td, wallU)
                    };
                }
                var lcU = new LineCase
                {
                    Base = pU, WallMm = wallU, UseMeasuredCurrent = false, CheckRamp = false,
                    SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                    FlangePlates = platesU,
                    ClampTempC = new[] { clampU, clampU, clampU, clampU }
                };

                Console.WriteLine("=== 整线轴向温度均匀度 ===");
                Console.WriteLine($"管壁 {wallU:0.0}／管保温 {tubeInsU:0}／盘Ø{2 * discU:0}／压接 {clampU:0} °C");
                var rU = LineRunner.Run(lcU);
                if (!rU.Ok) { Console.WriteLine("✗ " + rU.Message); return; }
                Console.WriteLine();

                Console.WriteLine($"{"段",8}{"控温°C",9}{"管温min",10}{"管温max",10}{"段内落差",10}" +
                                  $"{"玻璃入",9}{"玻璃出",9}{"管根ΔT",9}");
                double gMin = double.MaxValue, gMax = double.MinValue;
                double mMin = double.MaxValue, mMax = double.MinValue;
                foreach (var s in rU.Segments)
                {
                    if (s.TMetal.Length == 0) { Console.WriteLine($"{s.Name,8}  无剖面"); continue; }
                    double lo = s.TMetal.Min(), hi = s.TMetal.Max();
                    mMin = Math.Min(mMin, lo); mMax = Math.Max(mMax, hi);
                    gMin = Math.Min(gMin, s.TGlass.Length > 0 ? s.TGlass.Min() : double.MaxValue);
                    gMax = Math.Max(gMax, s.TGlass.Length > 0 ? s.TGlass.Max() : double.MinValue);
                    Console.WriteLine($"{s.Name,8}{s.SetpointC,9:0}{lo,10:0.0}{hi,10:0.0}{hi - lo,10:0.0}" +
                                      $"{s.GlassInC,9:0.0}{s.GlassOutC,9:0.0}{s.RootDeltaK,9:+0.0;−0.0}");
                }
                Console.WriteLine();
                // ⚠ 「整线 max−min」**不是**均匀度指标：三段控温点本来就是 1150/1080/1050，
                //   116 K 里绝大部分是**设计要的梯度**。真正的不均匀是**段内落差**。
                double worstSpan = rU.Segments.Where(s => s.TMetal.Length > 0)
                                              .Max(s => s.TMetal.Max() - s.TMetal.Min());
                Console.WriteLine($"★ **段内落差最大 {worstSpan:0.0} K** ← 这才是不均匀度");
                Console.WriteLine($"   （整线 {mMin:0.0}…{mMax:0.0} °C 的 {mMax - mMin:0.0} K 里，" +
                                  "绝大部分是控温点本身的梯度，不是缺陷）");
                Console.WriteLine($"   整线玻璃温 {gMin:0.0} … {gMax:0.0} °C");
                Console.WriteLine();
                Console.WriteLine($"⚠ 最低管温 {mMin:0.0} °C　vs 液相线 {p.TLiquidusC:0} °C" +
                                  $"　⇒ **析晶裕度 {mMin - p.TLiquidusC:+0.0;−0.0} K**");
                Console.WriteLine("   用户 2026-08-14 说先不考虑析晶，但这个数得记着：");
                Console.WriteLine($"   **HC3 的控温点 {rU.Segments[^1].SetpointC:0} °C 本身就等于液相线 {p.TLiquidusC:0} °C**");
                Console.WriteLine("   ⇒ 只要末段有任何冷点，它就在液相线以下。这是**给定工况自带的矛盾**，");
                Console.WriteLine("     不是本方案引入的 —— 要么抬高末段控温点，要么接受局部低于液相线。");
                Console.WriteLine();

                // 轴向剖面：每段沿长度采样，法兰在两端（x=0 与 x=L）
                Console.WriteLine("── 轴向剖面（每段 300 mm，两端即法兰所在）");
                Console.WriteLine($"{"段",8}{"x mm",8}{"管温°C",10}{"玻璃°C",10}{"距控温点",10}");
                foreach (var s in rU.Segments)
                {
                    if (s.TMetal.Length == 0) continue;
                    int n = s.TMetal.Length;
                    foreach (double frac in new[] { 0.0, 0.02, 0.05, 0.10, 0.25, 0.5, 0.75, 0.90, 0.95, 0.98, 1.0 })
                    {
                        int i = Math.Clamp((int)Math.Round(frac * (n - 1)), 0, n - 1);
                        Console.WriteLine($"{s.Name,8}{s.X[i],9:0.0}{s.TMetal[i],10:0.0}" +
                            $"{(s.TGlass.Length > i ? s.TGlass[i] : double.NaN),10:0.0}" +
                            $"{s.TMetal[i] - s.SetpointC,10:+0.0;−0.0}");
                    }
                    Console.WriteLine();
                }
                Console.WriteLine("★ 读法：坑集中在两端（法兰处）⇒ 补热要补在端部；");
                Console.WriteLine("  若中段也塌，那是整段功率不足，属另一回事。");
                Console.WriteLine();

                // ── 端部额外保温扫描：**方向交给模型定，我不预设**
                //   （管壁局部减薄那条更直接的路已被用户否掉：只能焊管，得不偿失）
                Console.WriteLine("── 端部额外保温扫描（**按 exp(−x/ℓt) 渐变**，作用长度 60 mm）");
                Console.WriteLine("⚠ 方向不预设：局部加保温与整体加保温对冷坑的作用**方向相反**，由实算判。");
                Console.WriteLine($"{"额外mm",8}{"段内落差max",13}{"最低管温",10}{"析晶裕度",10}" +
                                  $"{"C2 三段",26}{"② max",8}");
                // 粗扫（0/5/10/20/40）结论：**方向对，灵敏度极高** ——
                //   冷坑真没了（最低管温 1034→1049.8、析晶裕度 −16→−0.2），
                //   但 5 mm 时端部已从冷坑变成**热包**，段内落差反涨到 63 K。
                //   ⇒ 最优在 0–5 之间且很窄，改细扫。
                foreach (double ex in new[] { 0.0, 0.25, 0.5, 0.75, 1.0, 1.5, 2.0, 3.0 })
                {
                    var pE = SegmentSolver.Clone(pU);
                    // 作用长度放到 60 mm：渐变后厚度自己会衰减到零，长一点只是让尾巴更完整
                    pE.EndInsulExtraMm = ex; pE.EndInsulLengthMm = 60.0;
                    var lcE = new LineCase
                    {
                        Base = pE, WallMm = wallU, UseMeasuredCurrent = false, CheckRamp = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = platesU,
                        ClampTempC = new[] { clampU, clampU, clampU, clampU }
                    };
                    LineResult rE;
                    try { rE = LineRunner.Run(lcE); }
                    catch (Exception ex2) { Console.WriteLine($"{ex,8:0}  异常 {ex2.Message}"); continue; }
                    if (!rE.Ok) { Console.WriteLine($"{ex,8:0}  ✗ {rE.Message}"); continue; }

                    double span = rE.Segments.Where(s => s.TMetal.Length > 0)
                                             .Max(s => s.TMetal.Max() - s.TMetal.Min());
                    double lo2 = rE.Segments.Where(s => s.TMetal.Length > 0).Min(s => s.TMetal.Min());
                    double hi2 = rE.Segments.Where(s => s.TMetal.Length > 0).Max(s => s.TMetal.Max());
                    double e2m = rE.Flanges.Max(f => f.TMaxC - f.TRootC);
                    Console.WriteLine($"{ex,8:0.00}{span,13:0.0}{lo2,10:0.0}{lo2 - p.TLiquidusC,10:+0.0;−0.0}" +
                        $"{string.Join(" / ", rE.Segments.Select(s => s.RootDeltaK.ToString("+0.0;−0.0"))),26}" +
                        $"{e2m,8:+0.00;−0.00}  最高 {hi2:0.0}");
                }
                Console.WriteLine();
                Console.WriteLine("★ 看「段内落差」这一列往哪边走。若单调下降 ⇒ 端部加保温能填坑，");
                Console.WriteLine("  且它**不花铂**；若反而上升，说明整体那条 ΔT=D/√(kAβ) 的直觉在局部也成立。");
                return;
            }

            // --cli --clampscan   ★★★★ 压接温度：当前构型下唯一还没试过的自由度
            //
            // 为什么它可能同时救两条（先写下推理，再由实算判 —— 本项目手推已错三次）：
            // 舌片包了保温 ⇒ 自身散不掉，发的热几乎全得沿舌片流去压接端。
            // 那个轴向大热流正是**内部峰值**的来源（根部近乎绝热 ⇒ 温度先升后降）。
            // 压接端温度抬高 ⇒ 铜排抽走的少 ⇒ 轴向热流小 ⇒ 剖面平 ⇒ ② 降；
            // 同时整片需要发的热也少 ⇒ 舌片可以更厚 ⇒ 局部 J 更低 ⇒ ② 再降；
            // 且不必再从管子补 ⇒ C2 也松。三个方向似乎同号 —— **正因如此更要实算**。
            //
            // 代价在铜排一侧：压接端更热 ⇒ 散热段更长/更宽（见 --busbarplan）。
            if (args.Contains("--clampscan"))
            {
                double wallK = p.WeldMinThicknessMm, discK = 30.0;
                double clampLenK = 40.0, collarRK = 30.0, collarTK = 3.0;
                double discFloorK = WeldDistortion.ForPt(1.0, kb: 0.43).SlopePerB
                                    * (discK - 26.0) * p.WeldSafetyFactor;
                // --final2 收敛到的一组（R=30/3.0 环），固定不动，只扫压接温度
                double[] tabK = { 1.23, 1.91, 1.69, 0.90 }, insK = { 9.8, 1.2, 0.9, 2.0 };

                Console.WriteLine("=== 压接温度扫描（几何固定，只动压接端温度）===");
                Console.WriteLine($"管壁 {wallK:0.0}／盘Ø{2 * discK:0}／盘舌等厚／管孔环 R={collarRK:0} 厚 {collarTK:0.0}");
                Console.WriteLine($"舌厚 {string.Join("/", tabK.Select(v => v.ToString("0.00")))}" +
                                  $"　舌保温 {string.Join("/", insK.Select(v => v.ToString("0.0")))}");
                Console.WriteLine();
                Console.WriteLine($"{"压接°C",8}{"C2 三段 K",28}{"② 逐片",28}{"铜排带走 W 逐片",26}  ");

                foreach (double ct in new[] { 300.0, 450.0, 600.0, 750.0, 900.0 })
                {
                    var pK = SegmentSolver.Clone(p);
                    pK.Layer1.ThicknessMm = 10.0; pK.Layer1.Enabled = true;
                    pK.WallMinMm = wallK;
                    pK.FlangeInsulThickMm = 20; pK.FlangeInsulated = true;
                    pK.BusbarClampLengthMm = clampLenK; pK.BusbarClampTempC = ct;

                    var plates = new FlangePlate[4];
                    for (int j = 0; j < 4; j++)
                    {
                        double td = Math.Max(tabK[j], discFloorK);
                        plates[j] = new FlangePlate
                        {
                            DiscRadiusMm = discK, HoleRadiusMm = wallK + 25.0,
                            TabEndXMm = -90.0, TabEndHalfWidthMm = 15.0,
                            ThicknessMm = td, ThickenRadiusMm = collarRK,
                            ThickenedMm = Math.Max(collarTK, td),
                            TabThicknessMm = double.NaN,
                            InsulBoundaryXMm = double.NaN, TabInsulThickMm = insK[j],
                            TabParallel = true, TabFilletMm = 3.0,
                            WeldFilletLegMm = Math.Max(td, wallK)
                        };
                    }
                    var lcK = new LineCase
                    {
                        Base = pK, WallMm = wallK, UseMeasuredCurrent = false, CheckRamp = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = plates,
                        ClampTempC = new[] { ct, ct, ct, ct }
                    };
                    LineResult rK;
                    try { rK = LineRunner.Run(lcK); }
                    catch (Exception ex) { Console.WriteLine($"{ct,8:0}  异常 {ex.Message}"); continue; }
                    if (!rK.Ok) { Console.WriteLine($"{ct,8:0}  ✗ {rK.Message}"); continue; }

                    Console.WriteLine($"{ct,8:0}" +
                        $"{string.Join(" / ", rK.Segments.Select(s => s.RootDeltaK.ToString("+0.0;−0.0"))),28}" +
                        $"{string.Join("/", rK.Flanges.Select(f => (f.TMaxC - f.TRootC).ToString("+0.0;−0.0"))),28}" +
                        $"{string.Join("/", rK.Flanges.Select(f => f.QClampW.ToString("0"))),26}");
                }
                Console.WriteLine();
                Console.WriteLine("★ 若 ② 随压接温度升高而单调下降、C2 又不越界，");
                Console.WriteLine("  那本方案的最后一条硬约束就由**铜排的工作温度**买单，而不是靠加铂。");
                Console.WriteLine("  代价见 --busbarplan：压接端更热 ⇒ 散热段更长更宽（铜，不是铂）。");
                return;
            }

            // --cli --final2   ★★★★★ 双约束自动定尺寸：同时顶 C2 与判据 ②
            //
            // 上一版定尺寸器的**真缺陷**（--collar 暴露出来的）：它只盯 C2，对 ② 完全无视，
            // 而它「优先动保温、因为保温不花铂」的偏好，恰恰动的是**破坏 ② 的那个旋钮**。
            // 实测：装上管孔环后单片 ② 从 +2.32 降到 +0.00，可整线一收敛，
            // 保温被自动从 18.7/1.6/1.4/3.9 抬到 26.0/3.1/1.9/7.2 去补 C2，
            // ② 又被顶回 +2.58 —— 修好的地方被另一个旋钮亲手推翻。
            //
            // 改成**一旋钮对一约束**（按各自的主导方向分派）：
            //   · 舌片厚度 → C2：发热 ∝ 1/t ⇒ 抽热多就减薄
            //   · 舌片保温 → ②：保温厚 ⇒ 舌片热 ⇒ 峰值高 ⇒ ② 超了就减保温
            //   · 管孔加厚环 → 常驻：它是唯一「改善 ② 又不显著伤 C2」的自由度（§4.3k）
            //
            // ⚠ 两条约束**方向相冲**：改善 C2 的两个动作（加保温、减薄）都会恶化 ②。
            //   所以这不是两个独立的一维搜索，能不能同时满足是本轮真正要回答的问题。
            if (args.Contains("--final2"))
            {
                double wallF2 = p.WeldMinThicknessMm, discF2 = 30.0;
                double clampLenF2 = 40.0;
                // 外层扫压接温度：几何**跟着重新定尺寸**，这才是它真正的影响
                double clampF2 = 300.0;
                var frontRows = new List<(double clamp, double c2min, double c2max,
                                          double e2max, double mass, string where)>();
                // 前沿实测（每档都重新定过尺寸）：300 → ② +6.85（且 C2 顶到 +10.5）；
                //   **450 → ② +2.93（最好）**；600 → ② +6.33。
                // ⇒ `--clampscan` 那个「300 最优」是错的，因为它在**几何固定**下扫。
                // 压接温度定在 450，改扫**舌片半宽** —— 找那差着的 2.93 K。
                //
                // 为什么是宽度：尖峰由局部 J 定，而 J = I/(宽×厚)。
                // 加宽直接压 J，且**不像减薄那样牺牲发热**（发热 ∝ I·J·ρe·ℓ，
                // 加宽降 J 会降发热，但可由 C2 那个旋钮把厚度调回来补）。
                // 此前只扫过**窄**舌（§4.3b 的 30/16 mm），一次都没往宽里试过 ——
                // 因为那时的目标是「自给」，宽舌片散热面积大；
                // 但现在包了保温，面积的代价小了很多，取舍已经变了。
                // ★★ 半宽扫描（15/20/25）实测：② 反而单调变坏 +2.93 → +3.59 → +5.48。
                //   看收敛值就明白：半宽 25 时舌厚被削到 0.82、保温全顶到上限 80。
                //   **加宽降 J ⇒ 发热 ∝ I·J·ρe 跟着降 ⇒ 只能削薄+猛加保温补 C2 ⇒ ② 更坏。**
                //
                // ⇒ 把这一串失败归纳成一句：**C2 与 ② 通过 J 本身直接对立** ——
                //     P = I·J·ρe·ℓ，C2 要总发热够（要 J 高），② 要局部 J 低。
                //   凡是降 J 的动作（加宽、加厚、大环）都会被 C2 反推回来。
                //   式子里只剩 **ℓ（舌片长度）** 能在不动 J 的前提下加发热。
                //   ⇒ 本轮改扫舌长。（曾试过 130，但那次被单向棘轮 bug 污染，结论作废。）
                // ★★★★ 用户 2026-08-14 问「保温可以降吗？」—— 指到了我一直没动的那一条。
                //
                // 管根冷点深度（§6②）：|ΔT_dip| = D / √(k·A·β)，
                //   D = 法兰抽走的热，**β = 管自身的横向散热系数，在分母**。
                // ⇒ **管保温越薄，同样的抽热造成的冷点越浅。**
                // 这正好绕开死结：字面 ② 要求抽热 D 大（整圈都吸热），
                // 而 C2 限的是冷点深度 ΔT —— 降管保温可以让 D 大而 ΔT 不大。
                //
                // ⚠ `Layer1.ThicknessMm = 10` 是我从头钉死没动过的，而总纲里保温本就是
                //   四个自由度之一（「管与法兰分别；哪些部位要保温、保多厚」）。
                //   第八次「默认值当成给定条件」。
                // ⚠ 代价要一起看：管保温薄 ⇒ 散热大 ⇒ 电流大 ⇒ 管 J 升；
                //   且**玻璃温降会变** —— 那是全模型唯一拿现场实测（20 K）校准过的点。
                double halfWF2 = 15.0;
                double lenScaleF2 = 90.0;
                // 实测（靶=+5）：管保温 2/5/10/20 给 ② +2.83/+3.83/+2.93/+3.30 —— 方向对、幅度不够。
                // 因为靶钉在 +5，等于没去用薄保温换来的那份 D。本轮**两个一起动**。
                double tubeInsF2 = 10.0, c2TargetF2 = 9.0;
                foreach (var (insSweep, tgt) in new[]
                         { (2.0, 9.0), (3.0, 9.0), (5.0, 9.0), (10.0, 9.0) })
                {
                tubeInsF2 = insSweep; c2TargetF2 = tgt;
                clampF2 = 450.0;
                // 管孔加厚环：**绝对厚度，不是倍率**。
                // 第一版写成 ThickenedMm = 板厚 × 1.3，结果舌片被 C2 逼薄时环也跟着薄
                // （舌 1.61 ⇒ 环只有 2.09），压不住尖峰 —— 实测 HC2|HC3 保温已顶到下界 0.3、
                // ② 仍有 +5.4，没旋钮可用了。环的作用是把**局部 J** 压下去，
                // 它该由尖峰的强度定，与舌片被 C2 调到多厚无关。
                // ★ 管孔加厚做成**两级渐变**，不是单级环。`--taper` 单片筛（**同时报发热**）：
                //     无加厚          峰 +13.05　发热 225 W　+2.1 g
                //     单级 r≤30→3.0   峰  +1.51　发热 203 W　+28.8 g
                //     单级 r≤33→3.0   峰  +0.00　发热 185 W　+34.5 g
                //   **两级 30→2.4,36→1.7  峰 +0.00　发热 192 W　+22.1 g** ← 三项全优
                //   机理：高 J 区沿舌片延伸约 20 mm（§4.3k 的中线剖面），
                //   渐变在整段上都降 J；单级台阶只能在一个半径带上猛加厚，
                //   多出来的厚度按 K²/t 白白砍掉发热 —— 那正是 R=33 把 C2 炸到 +66 的原因。
                double[] stepRF2 = { 30.0, 36.0 }, stepTF2 = { 2.4, 1.7 };
                double discFloorF2 = WeldDistortion.ForPt(1.0, kb: 0.43).SlopePerB
                                     * (discF2 - 26.0) * p.WeldSafetyFactor;

                var pF2b = SegmentSolver.Clone(p);
                pF2b.Layer1.ThicknessMm = tubeInsF2; pF2b.Layer1.Enabled = true;
                pF2b.WallMinMm = wallF2;
                pF2b.FlangeInsulThickMm = 20; pF2b.FlangeInsulated = true;
                pF2b.BusbarClampLengthMm = clampLenF2; pF2b.BusbarClampTempC = clampF2;

                // 起点 = 无环时的收敛解。**不要另挑起点**：保温对 C2 的杠杆极大
                // （实测 18.7→3.3 把管根温差从 +9.9 推到 +51.8），从远处起步会先甩出可行域，
                // 再被两个相冲的旋钮来回推 —— 第一版从 ins=8 起步就是这么振荡的。
                // 装上管孔环后 ② 应从 +2.85 直接降约 2.3 K，只需在此附近做小幅牛顿修正。
                double[] tabF2 = { 1.37, 2.02, 1.80, 1.04 };
                double[] insF2v = { 18.7, 1.6, 1.4, 3.9 };

                // ★ **端片舌片加长**（总纲允许的自由度：「舌片长度如有需要可加长」）。
                //
                // 为什么只加端片：端片走单段电流（843/669 A），共用片走 √3 倍（1354/1201），
                // 发热 ∝ I² ⇒ 端片只有共用片的四成，而散热面积与铜排负荷一样（§4.3b）。
                // 定尺寸器为了给端片凑够发热，只能把它的舌片越削越薄 —— 实测入口片被削到
                // 1.09 mm 时 ② 冲到 +8.3。**薄是手段不是目的，真正要的是「总发热」。**
                //
                // 包了保温的舌片：发热 ∝ 长度（电阻 ∝ ℓ），而表面损失几乎不随长度涨（有保温），
                // 且自由段越长、压接端抽走的越少（∝ 1/ℓ_free）。⇒ 加长同时给出三样：
                // 更多发热、更少铜排失热、以及**可以把舌片留厚**（局部 J 低 ⇒ ② 好）。
                // ⚠ 加长端片舌片试过一次（130/90/90/130），结果被下面那个**单向棘轮** bug 污染，
                //   不能据此判断加长本身的好坏。控制器修好后要单独重试。先回到 90 隔离变量。
                double[] tabLenF2 = { 90.0, 90.0, 90.0, 90.0 };
                // ⚠ insHi 曾拍成 30 mm，实测入口片与出口片双双顶死在它上面 ——
                //   而总纲明写「**管外纤维没有空间限制；法兰可以完全不包，全依计算需求**」。
                //   又一次「默认值伪装成需求」（第六次，见 §4.3h/§4.3i）。放到 80。
                const double insLo = 0.3, insHi = 80.0;

                LineCase MakeF2(double[] tab, double[] ins, bool ramp)
                {
                    var plates = new FlangePlate[4];
                    for (int j = 0; j < 4; j++)
                    {
                        double td = Math.Max(tab[j], discFloorF2);
                        plates[j] = new FlangePlate
                        {
                            DiscRadiusMm = discF2, HoleRadiusMm = wallF2 + 25.0,
                            TabEndXMm = -lenScaleF2, TabEndHalfWidthMm = halfWF2,
                            ThicknessMm = td,
                            // 各级不得薄于板身（板被 C2 逼厚时，台阶不能反而成了减薄区）
                            DiscStepRadiiMm = stepRF2,
                            DiscStepThicknessMm = stepTF2.Select(v => Math.Max(v, td)).ToArray(),
                            TabThicknessMm = double.NaN,
                            InsulBoundaryXMm = double.NaN, TabInsulThickMm = ins[j],
                            TabParallel = true, TabFilletMm = 3.0,
                            WeldFilletLegMm = Math.Max(td, wallF2)
                        };
                    }
                    return new LineCase
                    {
                        Base = SegmentSolver.Clone(pF2b), WallMm = wallF2,
                        UseMeasuredCurrent = false, CheckRamp = ramp,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = plates,
                        ClampTempC = new[] { clampF2, clampF2, clampF2, clampF2 }
                    };
                }

                Console.WriteLine("=== 双约束自动定尺寸：C2 与判据 ② 同时顶 ===");
                Console.WriteLine($"管壁 {wallF2:0.0}／盘Ø{2 * discF2:0}／管孔两级渐变环 " +
                                  $"r≤{stepRF2[0]:0}→{stepTF2[0]:0.0}，r≤{stepRF2[1]:0}→{stepTF2[1]:0.0}／" +
                                  $"压接 {clampLenF2:0} 夹 {clampF2:0} °C");
                Console.WriteLine($"**管保温 {tubeInsF2:0} mm ＋ C2 靶 +{c2TargetF2:0}**（组合拳）　" +
                                  $"舌长 {lenScaleF2:0}／半宽 {halfWF2:0}");
                Console.WriteLine("分派：舌厚→C2（发热∝1/t）　舌保温→②（保温厚⇒舌片热⇒峰值高）");
                // 闭式（§4.3l）：把舌片当杆，Q_根 = kAΔT/ℓ − pℓ/2，T′(0) = −ΔT/ℓ + pℓ/(2kA)。
                // C2 要 Q_根>0 ⇔ T′(0)<0；② 要杆内无处高于管根 ⇔ 峰值不在内部 ⇔ T′(0)≤0。
                // **同一个不等式** ⇒ 两条不是对立的，中间那条带非空。
                // 实测里「既抽热又有一点更高」是二维局部尖峰叠在杆剖面上，杆模型看不见它 ——
                // 那正是管孔环要消掉的东西。环装上之后，② 就退化成 Q_根>0，与 C2 同向。
                Console.WriteLine("闭式（§4.3l）：② 与 C2 是同一个不等式的两侧，可行带非空；");
                Console.WriteLine("  管孔环消掉二维局部尖峰后，② 退化为「Q_根>0」，与 C2 同向。");
                Console.WriteLine();
                // ⚠ 只打 ②max 会让人（我）在盲调：实测保温从 18.7 减到 8.0、② 反而从
                //   +0.80 涨到 +1.19，光看 max 完全看不出是哪片、也看不出 T_max 与 T_root
                //   在同向漂。**逐片打出 ② 与管根温度**，才能判断旋钮到底有没有作用在它上面。
                Console.WriteLine($"{"轮",4}{"舌厚",22}{"保温",22}{"C2 三段 K",26}" +
                                  $"{"② 逐片（入/共1/共2/出）",30}{"管根°C 逐片",26}");

                LineResult? bestF2 = null; double bestBad = double.MaxValue;
                double[] bestTab = (double[])tabF2.Clone(), bestIns = (double[])insF2v.Clone();
                // 割线法的状态：上一轮的舌厚与 C2 误差，以及当前斜率估计（K per mm）
                var prevTab = new double[4]; var prevErr = new double[4]; var slopeEst = new double[4];
                var prevIns = new double[4]; var prevE2 = new double[4];
                var slopeInsC2 = new double[4];  // dC2误差/d保温 K/mm（负）
                for (int j = 0; j < 4; j++)
                {
                    prevTab[j] = double.NaN; prevErr[j] = 0; slopeEst[j] = 150.0;
                    prevIns[j] = double.NaN; prevE2[j] = 0; slopeInsC2[j] = -8.0;
                }

                for (int round = 0; round < 30; round++)
                {
                    LineResult rr;
                    try { rr = LineRunner.Run(MakeF2(tabF2, insF2v, false)); }
                    catch (Exception ex) { Console.WriteLine($"{round,4}  异常 {ex.Message}"); break; }
                    if (!rr.Ok) { Console.WriteLine($"{round,4}  ✗ {rr.Message}"); break; }

                    var dt = rr.Segments.Select(s => s.RootDeltaK).ToArray();
                    var e2 = rr.Flanges.Select(f => f.TMaxC - f.TRootC).ToArray();
                    double e2max = e2.Max();

                    // 违反度：C2 超界 + ② 超 0，两者同权重相加
                    double bad = dt.Sum(d => Math.Max(0, d - 10.0) + Math.Max(0, -d))
                               + Math.Max(0, e2max);
                    if (bad < bestBad)
                    {
                        bestBad = bad; bestF2 = rr;
                        bestTab = (double[])tabF2.Clone(); bestIns = (double[])insF2v.Clone();
                    }

                    Console.WriteLine($"{round,4}{string.Join("/", tabF2.Select(v => v.ToString("0.00"))),22}" +
                        $"{string.Join("/", insF2v.Select(v => v.ToString("0.0"))),22}" +
                        $"{string.Join(" / ", dt.Select(v => v.ToString("+0.0;−0.0"))),26}" +
                        $"{string.Join("/", e2.Select(v => v.ToString("+0.0;−0.0"))),30}" +
                        $"{string.Join("/", rr.Flanges.Select(f => f.TRootC.ToString("0"))),26}");

                    if (dt.All(d => d > 0 && d <= 10.0))
                    { Console.WriteLine($"   ⇒ C2 全过（② = {e2max:+0.00;−0.00}，由外层压接温度扫）"); break; }

                    bool moved = false;
                    for (int j = 0; j < 4; j++)
                    {
                        // ★★ 旋钮分派已按**实测**改过两次，记下来免得再绕：
                        //
                        // ① 「保温 → ②」**是错的**。实测（本命令逐片输出）：入口片保温从
                        //    18.7 减到 15.1，它的 ② 反而从 +0.0 涨到 +0.8 —— 方向不对。
                        //    ⇒ 保温**冻结**在无环收敛值，不再参与迭代。
                        // ② 「② 由管孔环控」才是对的（§4.3k/§4.3l）：环消掉二维局部尖峰，
                        //    之后 ② 退化成 Q_根>0，与 C2 同向 ⇒ 交给外层调环，不在这个内层。
                        // ③ 舌厚 → C2，但**斜率不能硬编码**：§4.2w 那个 1300 K/mm 是别的
                        //    构型下量的；本构型实测只有约 107 K/mm（1.37→1.34 使 C2 从
                        //    +24.1 到 +20.9）。硬编码 1300 ⇒ 每轮只挪 0.01 mm，30 轮走不到。
                        //    ⇒ 改**割线法在线量斜率**，首轮用保守默认值起步。

                        double e = 0; int c = 0;
                        // ★ C2 的**靶**是自由度，不是常数。原来钉在 +5（取「离悬崖远一点」），
                        //   而 C2 的上限是 10 —— 等于一直在「抽热尽量少」那一侧收敛。
                        //   字面 ② 要的恰恰相反：**抽热尽量多**（整圈都吸热才不会有放热象限）。
                        //   配合薄管保温（β 大 ⇒ 同样 D 冷点更浅），靶顶到 9 才是那条组合拳。
                        if (j - 1 >= 0 && j - 1 < dt.Length) { e += dt[j - 1] - c2TargetF2; c++; }
                        if (j < dt.Length) { e += dt[j] - c2TargetF2; c++; }
                        if (c == 0) continue;
                        e /= c;
                        if (Math.Abs(e) < 0.8) continue;

                        // ── ① 保温优先补 C2（**它不花铂**），顶死才动舌厚
                        //
                        // ★★ 内层**只解 C2**，不再试图同时控 ②。
                        //   我为「保温 ← ②」写过五版控制律，每一版都被实测否掉：
                        //   方向反、单向棘轮、乘法碾到下界、共用片上「② 只降一点点而 C2 崩掉」……
                        //   根因是这两条约束读的是**同一个量的两侧**（§4.3l 闭式），
                        //   用两个旋钮分别去追，等于在一根轴上互相拉扯。
                        //   ⇒ 内层解良定的那一条（C2），**② 只记录不控制**；
                        //     整条 ②–C2 前沿由外层扫**压接温度**画出来。
                        //
                        // ⚠ 这也顺带修掉 `--clampscan` 的方法错误：它在**几何固定**下扫压接温度，
                        //   而几何本该跟着重新定尺寸 —— 单变量扫耦合系统，和前面几次是同一个错。
                        // ⚠ 增益也必须割线，不能拍固定值。拍 0.35 mm/K 时实测：
                        //   共用片保温 1.6→8.2（+6.6 mm）把 C2 甩了 55 K，而当轮只需要 19 K
                        //   ⇒ 过冲 3 倍，C2 在 +43 与 −43 之间来回。（保温上限从 30 放到 80 之后
                        //   更明显 —— 上限原先在无意中当了限幅器。）
                        double sIns = slopeInsC2[j];              // dC2误差/d保温，物理上为负
                        if (!double.IsNaN(prevIns[j]) && Math.Abs(insF2v[j] - prevIns[j]) > 1e-6)
                        {
                            double s2 = (e - prevE2[j]) / (insF2v[j] - prevIns[j]);
                            if (s2 < -0.5 && s2 > -100) sIns = 0.5 * sIns + 0.5 * s2;
                        }
                        slopeInsC2[j] = sIns;
                        prevIns[j] = insF2v[j]; prevE2[j] = e;

                        double insWant = insF2v[j] - e / sIns;
                        double insNew = Math.Clamp(insWant, insLo, insHi);
                        bool insSaturated = Math.Abs(insNew - insWant) > 1e-9;
                        if (Math.Abs(insNew - insF2v[j]) > 1e-9) { insF2v[j] = insNew; moved = true; }

                        if (!insSaturated) continue;    // 保温还够用，先不动铂

                        // ── ③ 舌厚（割线，在线量斜率）
                        double slope = slopeEst[j];
                        if (!double.IsNaN(prevTab[j]) && Math.Abs(tabF2[j] - prevTab[j]) > 1e-6)
                        {
                            double s = (e - prevErr[j]) / (tabF2[j] - prevTab[j]);
                            if (s > 20 && s < 5000) slope = 0.5 * slope + 0.5 * s;
                        }
                        slopeEst[j] = slope;
                        prevTab[j] = tabF2[j]; prevErr[j] = e;

                        double dTab = Math.Clamp(-e / slope, -0.06, 0.06);
                        double nt = Math.Clamp(tabF2[j] + dTab, 0.3, 4.0);
                        if (Math.Abs(nt - tabF2[j]) > 1e-9) { tabF2[j] = nt; moved = true; }
                    }
                    if (!moved) { Console.WriteLine("   ⇒ 两个旋钮都到位或都顶死，停"); break; }
                }

                if (bestF2 == null) { Console.WriteLine("   ✗ 该压接温度下无解"); Console.WriteLine(); continue; }

                Console.WriteLine($"   舌厚 {string.Join("/", bestTab.Select(v => v.ToString("0.00")))}" +
                                  $"　舌保温 {string.Join("/", bestIns.Select(v => v.ToString("0.0")))}");
                var dtB = bestF2.Segments.Select(s => s.RootDeltaK).ToArray();
                var e2B = bestF2.Flanges.Select(f => f.TMaxC - f.TRootC).ToArray();
                int wj = 0; for (int q = 1; q < e2B.Length; q++) if (e2B[q] > e2B[wj]) wj = q;
                double mAll = bestF2.Segments.Sum(s => s.MassG) + bestF2.Flanges.Sum(f => f.MassG);
                Console.WriteLine($"   C2 {string.Join(" / ", dtB.Select(v => v.ToString("+0.0;−0.0")))} K" +
                                  $"　② max {e2B[wj]:+0.00;−0.00} K（{bestF2.Flanges[wj].Name}）" +
                                  $"　合计 {mAll:0} g");
                Console.WriteLine();
                double glassDrop = bestF2.Segments[0].GlassInC - bestF2.Segments[^1].GlassOutC;
                frontRows.Add((tubeInsF2, dtB.Min(), dtB.Max(), e2B[wj], mAll,
                    $"{bestF2.Flanges[wj].Name}／管J {bestF2.Segments.Max(s => s.TubeJAPerMm2):0.0}" +
                    $"／玻璃降 {glassDrop:0.0}（实测 20）"));

                // ── 完整判据表 + 逐片明细（交付件）
                Console.WriteLine("   ── 全判据复核（含升温规程）");
                try
                {
                    var rFull = LineRunner.Run(MakeF2(bestTab, bestIns, true));
                    if (rFull.Ok)
                    {
                        bestF2 = rFull;
                        Console.WriteLine($"   {"判据",-24}{"实际",10}{"限值",10}{"位置",10}  ");
                        foreach (var ck in rFull.Checks)
                            Console.WriteLine($"   {ck.Name,-24}{ck.Actual,10:0.00}{ck.Limit,10:0.00}" +
                                $"{ck.Where,10}  {(ck.Ok ? "✓" : "✗")}");
                        Console.WriteLine();
                        Console.WriteLine($"   {"片",10}{"电流A",8}{"板厚",7}{"舌保温",8}{"铜排W",8}" +
                                          $"{"抽管W",8}{"②K",8}{"②″K",8}{"峰温",7}{"克",7}");
                        for (int q = 0; q < rFull.Flanges.Length; q++)
                        {
                            var f = rFull.Flanges[q];
                            Console.WriteLine($"   {f.Name,10}{f.CurrentA,8:0}{bestTab[q],7:0.00}" +
                                $"{bestIns[q],8:0.0}{f.QClampW,8:0}{f.QFromTubeW,8:+0;−0}" +
                                $"{f.TMaxC - f.TRootC,8:+0.00;−0.00}" +
                                $"{(double.IsNaN(f.TDiscMaxC) ? 0 : f.TDiscMaxC - f.TRootC),8:+0.00;−0.00}" +
                                $"{f.TMaxC,7:0}{f.MassG,7:0}");
                        }
                        Console.WriteLine();
                        Console.WriteLine($"   {"段",10}{"控温°C",9}{"电流A",8}{"管根ΔT",10}{"管J",8}{"克",8}");
                        foreach (var s in rFull.Segments)
                            Console.WriteLine($"   {s.Name,10}{s.SetpointC,9:0}{s.CurrentA,8:0}" +
                                $"{s.RootDeltaK,10:+0.0;−0.0}{s.TubeJAPerMm2,8:0.00}{s.MassG,8:0}");
                    }
                }
                catch (Exception ex) { Console.WriteLine("   异常 " + ex.Message); }
                Console.WriteLine();
                }   // ← 压接温度外层循环结束

                Console.WriteLine("── ②–C2 前沿（每档都**重新定过尺寸**；压接 450 °C，扫**管保温**）");
                Console.WriteLine("★ 机理：|ΔT_dip| = D/√(kAβ)，管保温薄 ⇒ β 大 ⇒ 同样抽热下冷点更浅");
                Console.WriteLine($"{"管保温mm",9}{"C2 min",9}{"C2 max",9}{"② max",9}{"合计 g",9}  位置／管J／玻璃降");
                foreach (var fr in frontRows)
                    Console.WriteLine($"{fr.clamp,8:0}{fr.c2min,9:+0.0;−0.0}{fr.c2max,9:+0.0;−0.0}" +
                        $"{fr.e2max,9:+0.00;−0.00}{fr.mass,9:0}  " +
                        (fr.c2min > 0 && fr.c2max <= 10 && fr.e2max <= 0 ? "✓ 两条都过"
                         : fr.c2min > 0 && fr.c2max <= 10 ? $"C2 过，② 差 {fr.e2max:0.00}（{fr.where}）"
                         : "C2 未过"));
                Console.WriteLine();
                Console.WriteLine("★ 若所有档位都是「C2 过、② 差一点」，那说明在本构型下两条约束的可行带为空，");
                Console.WriteLine("  差额就是还需要另外找的那部分 —— 而不是再调这两个旋钮能补上的。");
                return;
            }

            // --cli --collar   ★★★★ 判据 ② 的对策：管孔加厚环
            //
            // --hotspot 量明白了：② 不是整片过热，是**舌片根部紧贴管孔那一格**的局部尖峰
            //（x=−28.3, r=28.3, J=25.9, 超管根 +2.32 K），往外 2 mm 就掉到 −6.4 K。
            // 而圆盘 θ≤135° 处 J=0（电学死区）⇒ 与「绕流」无关，双舌片救不了（§4.3j 实算已证）。
            //
            // 面电流守恒 K=J·t ⇒ 局部加厚**同时**压低该处的 J 与单位面积发热（∝K²/t），
            // 这是唯一直接作用在尖峰上的自由度。现有焊脚只加厚到 r≈27.4 就用完，
            // 峰值恰好落在它外面一格 —— 把环延出去即可。
            //
            // 先用**单片筛**扫网格（秒级，定管根定电流），再把优胜者送整线复核。
            if (args.Contains("--collar"))
            {
                double wallC9 = p.WeldMinThicknessMm, discC9 = 30.0;
                double clampLenC9 = 40.0, clampC9 = 300.0;
                double[] tabC9 = { 1.37, 2.02, 1.80, 1.04 }, insC9 = { 18.7, 1.6, 1.4, 3.9 };
                double discFloorC9 = WeldDistortion.ForPt(1.0, kb: 0.43).SlopePerB
                                     * (discC9 - 26.0) * p.WeldSafetyFactor;

                var pC9 = SegmentSolver.Clone(p);
                pC9.Layer1.ThicknessMm = 10.0; pC9.Layer1.Enabled = true;
                pC9.WallMinMm = wallC9;
                pC9.FlangeInsulThickMm = 20; pC9.FlangeInsulated = true;
                pC9.BusbarClampLengthMm = clampLenC9; pC9.BusbarClampTempC = clampC9;

                // ⚠ 厚度必须**显式传入**：外层双旋钮迭代改的是 tab2，若这里闭包读 tabC9，
                //   板厚会跟着变、而 ThickenedMm/WeldFilletLegMm 仍用旧值 —— 静默的不一致。
                FlangePlate MkC9(int j, double collarR, double collarT, double tabMm)
                {
                    double td = Math.Max(tabMm, discFloorC9);
                    return new FlangePlate
                    {
                        DiscRadiusMm = discC9, HoleRadiusMm = wallC9 + 25.0,
                        TabEndXMm = -90.0, TabEndHalfWidthMm = 15.0,
                        ThicknessMm = td,
                        // 等厚板（TabThicknessMm=NaN）⇒ 加厚环按**半径**生效，
                        // 会同时覆盖圆盘与舌片根部 —— 尖峰正在那里，这是要的行为。
                        ThickenRadiusMm = collarR, ThickenedMm = collarT > 0 ? collarT : td,
                        TabThicknessMm = double.NaN,
                        InsulBoundaryXMm = double.NaN, TabInsulThickMm = insC9[j],
                        TabParallel = true, TabFilletMm = 3.0,
                        WeldFilletLegMm = Math.Max(td, wallC9)
                    };
                }

                Console.WriteLine("=== 判据 ② 的对策：管孔加厚环 ===");
                Console.WriteLine("先跑一次基线整线，取 ② 最差那片的真实电流与管根温度。");
                var lcBase = new LineCase
                {
                    Base = SegmentSolver.Clone(pC9), WallMm = wallC9,
                    UseMeasuredCurrent = false, CheckRamp = false,
                    SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                    FlangePlates = new[] { MkC9(0, 0, 0, tabC9[0]), MkC9(1, 0, 0, tabC9[1]),
                                          MkC9(2, 0, 0, tabC9[2]), MkC9(3, 0, 0, tabC9[3]) },
                    ClampTempC = new[] { clampC9, clampC9, clampC9, clampC9 }
                };
                var rBase = LineRunner.Run(lcBase);
                if (!rBase.Ok) { Console.WriteLine("✗ " + rBase.Message); return; }
                int jw2 = 0;
                for (int j = 1; j < rBase.Flanges.Length; j++)
                    if (rBase.Flanges[j].TMaxC - rBase.Flanges[j].TRootC
                        > rBase.Flanges[jw2].TMaxC - rBase.Flanges[jw2].TRootC) jw2 = j;
                var fB = rBase.Flanges[jw2];
                Console.WriteLine($"⇒ {fB.Name}：I={fB.CurrentA:0} A，管根 {fB.TRootC:0.0} °C，" +
                                  $"基线 ② = {fB.TMaxC - fB.TRootC:+0.00;−0.00} K");
                Console.WriteLine();

                double tBase = Math.Max(tabC9[jw2], discFloorC9);
                Console.WriteLine($"── 单片筛（定管根 {fB.TRootC:0.0} °C、定电流 {fB.CurrentA:0} A）");
                Console.WriteLine($"基板厚 {tBase:0.00} mm；加厚环 = 半径 ≤ R 处取厚 t（与焊脚叠加）");
                Console.WriteLine($"{"环R mm",9}{"环厚 mm",10}{"T峰−管根 K",13}{"J峰",8}{"增重 g/片",11}  ");

                (double R, double T, double dm, double peak)? best = null;
                foreach (double cr in new[] { 0.0, 30.0, 33.0, 36.0, 40.0 })
                    foreach (double ct in cr <= 0 ? new[] { 0.0 }
                                          : new[] { tBase * 1.3, tBase * 1.6, tBase * 2.0 })
                    {
                        var g = MkC9(jw2, cr, ct, tabC9[jw2]);
                        ShellMesh mm2;
                        try { mm2 = FlangeMesher.Build(g, 0, 2.0, 11.0, 45.0, clampLenC9); }
                        catch (Exception ex) { Console.WriteLine($"{cr,9:0}{ct,10:0.00}  网格失败 {ex.Message}"); continue; }
                        var sc2 = ShellCurrent.Solve(mm2, fB.CurrentA,
                                      Materials.PtResistivity(fB.TRootC) * 1e3, fB.TRootC);
                        var th2 = ShellThermal.Solve(mm2, sc2.JMagAPerMm2, pC9, fB.TRootC,
                                      g.InsulBoundaryXResolved, tabBoundaryX: g.Tangent().X,
                                      tabInsulThickMm: g.TabInsulThickMm);
                        double tPk = th2.T.Max(), jPk = sc2.JMagAPerMm2.Max();
                        // 增重：环内多出来的那层（环面积 × 增厚），铂 21.45 g/cm³
                        double dm = cr <= 0 ? 0
                            : Math.PI * (cr * cr - g.HoleRadiusMm * g.HoleRadiusMm)
                              * Math.Max(0, ct - tBase) * 1e-3 * 21.45;
                        Console.WriteLine($"{(cr <= 0 ? "无" : cr.ToString("0")),9}" +
                            $"{(cr <= 0 ? "—" : ct.ToString("0.00")),10}" +
                            $"{tPk - fB.TRootC,13:+0.00;−0.00}{jPk,8:0.0}{dm,11:0.0}");
                        if (best == null || tPk < best.Value.peak) best = (cr, ct, dm, tPk);
                    }

                Console.WriteLine();
                if (best == null) { Console.WriteLine("✗ 无可用结果"); return; }
                Console.WriteLine("★ 单片筛只说明「尖峰能不能压下去」。加厚会同时降低整片发热 ⇒");
                Console.WriteLine("  C2（管根温差）会跟着漂，必须回整线用双旋钮重新收敛。");
                Console.WriteLine();

                // 把**筛出来的那一组**送整线复核（不要只取半径再自行拍厚度）
                double bR = best.Value.R, bT = best.Value.T;
                Console.WriteLine($"── 整线复核：四片同装 R={bR:0} / 厚 {bT:0.00} 的加厚环");
                double[] ins2 = (double[])insC9.Clone(), tab2 = (double[])tabC9.Clone();
                LineResult? lastC = null;
                for (int round = 0; round < 16; round++)
                {
                    var plates = new FlangePlate[4];
                    for (int j = 0; j < 4; j++)
                    {
                        double td = Math.Max(tab2[j], discFloorC9);
                        // 环厚按「相对基板的倍率」跟着走，否则舌片一变薄环就成了巨大台阶
                        double ratio = bR <= 0 ? 1.0 : bT / Math.Max(1e-9, tBase);
                        plates[j] = MkC9(j, bR, bR <= 0 ? 0 : td * ratio, tab2[j]);
                        plates[j].TabInsulThickMm = ins2[j];
                    }
                    var lc = new LineCase
                    {
                        Base = SegmentSolver.Clone(pC9), WallMm = wallC9,
                        UseMeasuredCurrent = false, CheckRamp = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = plates,
                        ClampTempC = new[] { clampC9, clampC9, clampC9, clampC9 }
                    };
                    LineResult rr2;
                    try { rr2 = LineRunner.Run(lc); }
                    catch (Exception ex) { Console.WriteLine($"   异常 {ex.Message}"); break; }
                    if (!rr2.Ok) { Console.WriteLine($"   ✗ {rr2.Message}"); break; }
                    lastC = rr2;
                    var dt = rr2.Segments.Select(s => s.RootDeltaK).ToArray();
                    if (dt.All(d => d > 0 && d <= 10.0)) break;

                    bool moved = false;
                    for (int j = 0; j < 4; j++)
                    {
                        double e = 0; int c = 0;
                        if (j - 1 >= 0 && j - 1 < dt.Length) { e += dt[j - 1] - 5.0; c++; }
                        if (j < dt.Length) { e += dt[j] - 5.0; c++; }
                        if (c == 0) continue;
                        e /= c;
                        if (Math.Abs(e) < 1.0) continue;
                        double want = Math.Clamp(ins2[j] + 0.06 * e, 0.5, 30.0);
                        if (Math.Abs(want - ins2[j]) > 1e-9) { ins2[j] = want; moved = true; continue; }
                        double step = Math.Clamp(0.004 * Math.Abs(e), 0.01, 0.15);
                        double t2v = e < 0 ? tab2[j] * (1 + step) : tab2[j] * (1 - step);
                        double nv = Math.Clamp(t2v, 0.3, 4.0);
                        if (Math.Abs(nv - tab2[j]) > 1e-9) { tab2[j] = nv; moved = true; }
                    }
                    if (!moved) break;
                }
                if (lastC != null)
                {
                    Console.WriteLine($"   舌厚 {string.Join("/", tab2.Select(v => v.ToString("0.00")))}" +
                                      $"　保温 {string.Join("/", ins2.Select(v => v.ToString("0.0")))}");
                    Console.WriteLine($"   管根ΔT {string.Join(" / ", lastC.Segments.Select(s => s.RootDeltaK.ToString("+0.0;−0.0")))} K" +
                        $"　合计 **{lastC.Segments.Sum(s => s.MassG) + lastC.Flanges.Sum(f => f.MassG):0} g**");
                    foreach (var ck in lastC.Checks.Where(x => x.Kind == CheckKind.HardSafety))
                        Console.WriteLine($"   {(ck.Ok ? "✓" : "✗")} {ck.Name,-22}{ck.Actual,9:+0.00;−0.00} / {ck.Limit,6:0.00}  {ck.Where}");
                }
                return;
            }

            // --cli --busbarplan   ★★★★ 铜排的长宽高 + 在舌片上的位置（用户 2026-08-14：
            //   「铜排尺寸(长宽高)与排布(分布在舌的位置)必须同时给出」）
            //
            // 此前只给了「截面 873 mm²」——那不是可施工的信息：截面不等于长宽高，
            // 而且没说夹在舌片的哪一段。本条把四片各自的**完整铜排**一次给全，
            // 且**位置与热平衡是耦合的**：压接段占掉舌片的一截，剩下的自由段长度
            // 决定引线漏热（§4.3e 的 ΔT/ℓ），所以位置不能事后再定。
            //
            // 铜排按**风冷散热片**定尺寸，不是「导到某个恒温冷端」——现场没有冷端（§4.5）。
            if (args.Contains("--busbarplan"))
            {
                double wallB9 = p.WeldMinThicknessMm, discB9 = 30.0;
                double clampLenB9 = 40.0, clampB9 = 450.0;   // 定案值（--final2 前沿：450 优于 300）
                double tabLB9 = 90.0, halfWB9 = 15.0;
                double[] tabB9 = { 1.37, 2.02, 1.80, 1.04 }, insB9 = { 18.7, 1.6, 1.4, 3.9 };
                double discFloorB9 = WeldDistortion.ForPt(1.0, kb: 0.43).SlopePerB
                                     * (discB9 - 26.0) * p.WeldSafetyFactor;

                var pB9 = SegmentSolver.Clone(p);
                pB9.Layer1.ThicknessMm = 10.0; pB9.Layer1.Enabled = true;
                pB9.WallMinMm = wallB9;
                pB9.FlangeInsulThickMm = 20; pB9.FlangeInsulated = true;
                pB9.BusbarClampLengthMm = clampLenB9; pB9.BusbarClampTempC = clampB9;

                FlangePlate MkB9(int j)
                {
                    double td = Math.Max(tabB9[j], discFloorB9);
                    return new FlangePlate
                    {
                        DiscRadiusMm = discB9, HoleRadiusMm = wallB9 + 25.0,
                        TabEndXMm = -tabLB9, TabEndHalfWidthMm = halfWB9,
                        ThicknessMm = td, ThickenedMm = td, TabThicknessMm = double.NaN,
                        InsulBoundaryXMm = double.NaN, TabInsulThickMm = insB9[j],
                        TabParallel = true, TabFilletMm = 3.0,
                        WeldFilletLegMm = Math.Max(td, wallB9)
                    };
                }
                var lcB9 = new LineCase
                {
                    Base = SegmentSolver.Clone(pB9), WallMm = wallB9,
                    UseMeasuredCurrent = false, CheckRamp = false,
                    SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                    FlangePlates = new[] { MkB9(0), MkB9(1), MkB9(2), MkB9(3) },
                    ClampTempC = new[] { clampB9, clampB9, clampB9, clampB9 }
                };

                Console.WriteLine("=== 铜排：长宽高 + 在舌片上的位置 ===");
                Console.WriteLine("先跑一次定案整线，取每片**真实**的电流与铜排带走的热，再据此定尺寸。");
                var rB9 = LineRunner.Run(lcB9);
                if (!rB9.Ok) { Console.WriteLine("✗ " + rB9.Message); return; }

                double xTangent = MkB9(0).Tangent().X;
                Console.WriteLine();
                Console.WriteLine("── ① 排布（四片相同，由几何定）");
                Console.WriteLine($"舌片：自圆盘切点 x={xTangent:0.0} 伸到末端 x=−{tabLB9:0}，等宽 {2 * halfWB9:0} mm");
                Console.WriteLine($"压接段：**自舌片末端往回 {clampLenB9:0} mm**，即 x ∈ [−{tabLB9:0}, −{tabLB9 - clampLenB9:0}]");
                Console.WriteLine($"自由段：x ∈ [{xTangent:0.0}, −{tabLB9 - clampLenB9:0}]，长 {(-(tabLB9 - clampLenB9)) - xTangent:0.0} mm（取绝对值 {Math.Abs(-(tabLB9 - clampLenB9) - xTangent):0.0}）");
                Console.WriteLine("★ 两面夹（上下各一块铜排）⇒ 接触面积翻倍，压接界面 J 减半。");
                Console.WriteLine("★ 压接段**必须在末端**：它是 300 °C 的冷边界，越靠近圆盘，");
                Console.WriteLine("  自由段越短 ⇒ 引线漏热 ∝ 1/ℓ 越大 ⇒ 直接把管根抽冷（§4.3e）。");
                Console.WriteLine();

                Console.WriteLine("── ② 尺寸（风冷散热片模型：热从压接端进，沿程对流+辐射散掉）");
                Console.WriteLine($"环境 {p.TAmbC:0} °C／压接端 {clampB9:0} °C／铜排等宽舌片 {2 * halfWB9:0} mm／");
                Console.WriteLine("表面取氧化铜 ε=0.7（★ 抛光铜仅 0.05，**差 14 倍**，务必按实际表面取）");
                Console.WriteLine();
                Console.WriteLine("每片两块铜排（上下夹）。压接块宽度被舌片锁死，散热段可 flare 到更宽更薄。");
                Console.WriteLine($"{"片",10}{"电流A",8}{"带走W",8}{"压接块 宽×长×厚",20}" +
                                  $"{"散热段 宽×厚×长",22}{"铜排J",8}{"压接J",8}{"铜 kg",8}  判定");

                for (int j = 0; j < rB9.Flanges.Length; j++)
                {
                    var f = rB9.Flanges[j];
                    var fin = BusbarSizing.SizeAirCooledFin(
                        currentA: f.CurrentA, qFromPtW: f.QClampW,
                        tabWidthMm: 2 * halfWB9, clampLenMm: clampLenB9,
                        clampTempC: clampB9, tAmbC: p.TAmbC,
                        jBusAllow: 3.0, jContactAllow: 1.0,
                        emissivity: 0.7, doubleSided: true);
                    string clampDim = $"{fin.ClampWidthMm:0}×{fin.ClampLenMm:0}×{fin.ClampThickMm:0.0}";
                    string finDim = $"{fin.FinWidthMm:0}×{fin.FinThickMm:0.0}×{fin.FinLengthMm:0}";
                    Console.WriteLine($"{f.Name,10}{f.CurrentA,8:0}{f.QClampW,8:0}{clampDim,20}" +
                        $"{finDim,22}{fin.JBusAPerMm2,8:0.00}{fin.JContactAPerMm2,8:0.00}" +
                        $"{fin.CopperKg,8:0.00}  " + (fin.Ok ? "✓" : "✗ ") + fin.Note);
                }
                Console.WriteLine();
                Console.WriteLine("读法：");
                Console.WriteLine("· **散热段越薄越省铜**：V ∝ Q·t（推导见 BusbarSizing.SizeAirCooledFin 注释）");
                Console.WriteLine("  ⇒ 厚度取到**载流刚好卡住**（3 A/mm²）为止，宽度再按散热需求定。");
                Console.WriteLine("· **长度**由「散得完」定：取 L = 3/m，m = √(hP/(kA))，再长几乎无增益（tanh3=0.995）。");
                Console.WriteLine("· 压接界面 J 若超 1.0，加长压接段或改多点压接；**不要靠加压紧力硬扛**。");
                Console.WriteLine("· 这套长度若在现场排不下，等价做法是**加风**（h 从 8 提到 30 可把长度砍一半），");
                Console.WriteLine("  但 §4.5 已算过：夹持温度 400 vs 80 °C 只差 10 W / 18 K ⇒ 不需要水冷。");
                return;
            }

            // --cli --wallfloor   ★★★ 管壁的**物理**下界在哪：把「升温墙」钉死
            //
            // --weldvalue 发现壁厚 0.30 时空管**给无限长时间也升不到目标**
            // （判据①返回 Reached=false，是渐近线在目标之下，不是超时），而 0.50 过。
            // ⇒ 真正的管壁下界落在 (0.30, 0.50)，**由物理定不由焊接定**。这条就是去钉它。
            //
            // 机理：可用功率 P = I²·ρe·L/A，而电流上界被热稳定极限压住
            //   I_stab = √(βA/(dρe/dT)) ⇒ P_max ∝ A ∝ 壁厚，
            // 而热损失基本不随壁厚变 ⇒ 壁薄到某处，P_max 追不上损失，温度停在半路。
            //
            // 这里只解一次整线（不跑 C2 定点迭代）：判据①主要由管本身定，
            // 法兰细节是二阶量。**是筛子不是定案**，过了的壁厚仍要回 --weldvalue 复核。
            if (args.Contains("--wallfloor"))
            {
                double discL9 = 30.0;
                double discFloorL9 = WeldDistortion.ForPt(1.0, kb: 0.43).SlopePerB
                                     * (discL9 - 26.0) * p.WeldSafetyFactor;

                Console.WriteLine("=== 管壁的物理下界：升温墙在哪 ===");
                Console.WriteLine($"判据①「空管升到目标」，限时 = 用户给的 **≤3 天（{new LineCase().RampHours:0} h）**。");
                Console.WriteLine("⚠ 2026-08-14 更正：此前限时写死 3 h，把「升得慢」误报成「升不到」，");
                Console.WriteLine("  据此得出的「管壁 0.30 是硬物理墙」是错的（见 §4.3i）。");
                Console.WriteLine("⚠ 本表是筛子：只解一次整线，不跑 C2 定点迭代。");
                Console.WriteLine();
                // ★★ 两档并排：J_allow=10 是**占位值**（§4.2i 已把它降为参考量，基准存疑），
                //    真正的物理上限是空管热稳定极限 I_stab。若「墙」只在占位值下出现，
                //    那它就不是物理墙 —— 这一列是用来分辨这件事的。
                Console.WriteLine($"{"管壁",7}{"段电流A",10}{"管J",8}{"升温(J≤10)",13}" +
                                  $"{"升温(仅热稳定)",16}{"管 g",8}  说明");

                foreach (double wl in new[] { 0.60, 0.50, 0.45, 0.40, 0.35, 0.30, 0.25, 0.20, 0.15 })
                {
                    var pL9 = SegmentSolver.Clone(p);
                    pL9.Layer1.ThicknessMm = 10.0; pL9.Layer1.Enabled = true;
                    pL9.WallMinMm = wl;
                    pL9.FlangeInsulThickMm = 20; pL9.FlangeInsulated = true;
                    pL9.BusbarClampLengthMm = 40; pL9.BusbarClampTempC = 300;

                    // 法兰按 0.6 档收敛解、舌厚 ∝ 壁厚外推（发热 ∝ 壁厚/t ⇒ 等发热要 t ∝ 壁厚）
                    double scl = wl / 0.60;
                    double[] tabL = { 1.37 * scl, 2.02 * scl, 1.80 * scl, 1.04 * scl };
                    double[] insL = { 16.5, 0.9, 1.1, 2.9 };
                    var platesL = new FlangePlate[4];
                    for (int j = 0; j < 4; j++)
                    {
                        double tD = Math.Max(tabL[j], discFloorL9);
                        platesL[j] = new FlangePlate
                        {
                            DiscRadiusMm = discL9, HoleRadiusMm = wl + 25.0,
                            TabEndXMm = -90.0, TabEndHalfWidthMm = 15.0,
                            ThicknessMm = tD, ThickenedMm = tD, TabThicknessMm = double.NaN,
                            InsulBoundaryXMm = double.NaN, TabInsulThickMm = insL[j],
                            TabParallel = true, TabFilletMm = 3.0,
                            WeldFilletLegMm = Math.Max(tD, wl)
                        };
                    }
                    var lcL9 = new LineCase
                    {
                        Base = pL9, WallMm = wl, UseMeasuredCurrent = false, CheckRamp = true,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = platesL,
                        ClampTempC = new[] { 300.0, 300.0, 300.0, 300.0 }
                    };

                    string Ramp(double jAllow)
                    {
                        var pv = SegmentSolver.Clone(pL9);
                        pv.JAllowAPerMm2 = jAllow;
                        var lcv = new LineCase
                        {
                            Base = pv, WallMm = wl, UseMeasuredCurrent = false, CheckRamp = true,
                            SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                            FlangePlates = platesL,
                            ClampTempC = new[] { 300.0, 300.0, 300.0, 300.0 }
                        };
                        try
                        {
                            var rv = LineRunner.Run(lcv);
                            if (!rv.Ok) return "解失败";
                            var ck = rv.Checks.FirstOrDefault(x => x.Name.StartsWith("① 升温"));
                            return ck == null ? "—"
                                 : double.IsNaN(ck.Actual) ? "✗ 到不了" : $"{ck.Actual:0.0} h";
                        }
                        catch { return "异常"; }
                    }

                    LineResult rL9;
                    try { rL9 = LineRunner.Run(lcL9); }
                    catch (Exception ex) { Console.WriteLine($"{wl,7:0.00}  异常 {ex.Message}"); continue; }
                    if (!rL9.Ok) { Console.WriteLine($"{wl,7:0.00}  ✗ {rL9.Message}"); continue; }

                    var ramp = rL9.Checks.FirstOrDefault(x => x.Name.StartsWith("① 升温"));
                    string txt = ramp == null ? "—"
                        : double.IsNaN(ramp.Actual) ? "✗ 到不了" : $"{ramp.Actual:0.0} h";
                    Console.WriteLine($"{wl,7:0.00}{rL9.Segments.Max(s => s.CurrentA),10:0}" +
                        $"{rL9.Segments.Max(s => s.TubeJAPerMm2),8:0.00}{txt,13}" +
                        $"{Ramp(1000.0),16}{rL9.Segments.Sum(s => s.MassG),8:0}  " + (ramp?.Note ?? ""));
                }
                Console.WriteLine();
                Console.WriteLine("★ 两列若在同一档翻脸 ⇒ 那是**物理墙**（热稳定极限），管壁下界成立。");
                Console.WriteLine("  若只有左列翻脸而右列还过 ⇒ 墙是 J_allow=10 这个**占位值**造的，");
                Console.WriteLine("  不是物理 ⇒ 得先把 J 的真实限值定下来（§6 待补），才谈得上管壁下界。");
                return;
            }

            // --cli --twotab   ★★★★ 双舌片：判据 ② 唯一还没试过的结构性杠杆
            //
            // 定案方案唯一不过的就是 ②（法兰最高温 − 管温 = +2.85 K）与 ②″（圆盘区 +1.67）。
            // 两者是**同一个成因**：电流从单侧舌片进来、绕过管孔，在靠舌片那一侧堆成峰值
            // （§4.6 实测 J_max/J_rms = 2.17），而单位面积发热 ∝ J² ⇒ 那一点局部过热。
            // 调保温、调厚度都改不了这个**分布**问题 —— 它们只改总量。
            //
            // 双舌片（180° 对置）直接消掉不对称：两个舌端同为 V=1，电流自然对称分流。
            // ★ 关键是它**几乎不花铂、也不动能量账**：每舌走 I/2，取半宽 ⇒ 总截面不变，
            //   总发热 = 2·(I/2)·J·ρe·ℓ = I·J·ρe·ℓ 不变，总散热面积也不变。
            //   变的只有**峰值**：局部 J 减半 ⇒ 局部发热降到 1/4。
            // 附带好处：压接界面 J 减半（§4.3a 记的 9.2 A/mm² 那条也跟着缓解）。
            // 代价：每片要两根铜排。
            if (args.Contains("--twotab"))
            {
                double wallT9 = p.WeldMinThicknessMm, holeT9 = wallT9 + 25.0;
                double discT9 = 30.0, clampLenT9 = 40.0, clampT9 = 300.0, targetT9 = 5.0;
                double discFloorT9 = WeldDistortion.ForPt(1.0, kb: 0.43).SlopePerB
                                     * (discT9 - 26.0) * p.WeldSafetyFactor;

                Console.WriteLine("=== 单舌片 vs 双舌片：能不能把判据 ② 救回来 ===");
                Console.WriteLine($"管壁 {wallT9:0.0}／盘Ø{2 * discT9:0}／盘舌等厚／压接 {clampLenT9:0} 夹 {clampT9:0} °C");
                Console.WriteLine("★ 双舌片取**半宽减半**（15→7.5）⇒ 总截面、总发热、总散热面积都不变，");
                Console.WriteLine("  只有峰值变：每舌走 I/2 ⇒ 局部 J 减半 ⇒ 局部发热降到 1/4。");
                Console.WriteLine();

                var pT9 = SegmentSolver.Clone(p);
                pT9.Layer1.ThicknessMm = 10.0; pT9.Layer1.Enabled = true;
                pT9.WallMinMm = wallT9;
                pT9.FlangeInsulThickMm = 20; pT9.FlangeInsulated = true;
                pT9.BusbarClampLengthMm = clampLenT9;
                pT9.BusbarClampTempC = clampT9;

                foreach (var (nm2, two, halfW) in new[]
                         { ("单舌片 半宽15", false, 15.0), ("双舌片 各半宽7.5", true, 7.5) })
                {
                    double[] insT9 = { 16.5, 0.9, 1.1, 2.9 };
                    double[] tabT9 = { 1.37, 2.02, 1.80, 1.04 };

                    LineCase MakeT9(double[] ins, double[] tab)
                    {
                        var plates = new FlangePlate[4];
                        for (int j = 0; j < 4; j++)
                        {
                            double tDisc = Math.Max(tab[j], discFloorT9);
                            plates[j] = new FlangePlate
                            {
                                DiscRadiusMm = discT9, HoleRadiusMm = holeT9,
                                TabEndXMm = -90.0, TabEndHalfWidthMm = halfW,
                                ThicknessMm = tDisc, ThickenedMm = tDisc,
                                TabThicknessMm = double.NaN,
                                InsulBoundaryXMm = double.NaN, TabInsulThickMm = ins[j],
                                TabParallel = true, TabFilletMm = 3.0, TwoTabs = two,
                                WeldFilletLegMm = Math.Max(tDisc, wallT9)
                            };
                        }
                        return new LineCase
                        {
                            Base = SegmentSolver.Clone(pT9), WallMm = wallT9,
                            UseMeasuredCurrent = false, CheckRamp = false,
                            SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                            FlangePlates = plates,
                            ClampTempC = new[] { clampT9, clampT9, clampT9, clampT9 }
                        };
                    }

                    Console.WriteLine($"── {nm2}");
                    LineResult? lastT9 = null;
                    for (int round = 0; round < 20; round++)
                    {
                        LineResult rT9;
                        try { rT9 = LineRunner.Run(MakeT9(insT9, tabT9)); }
                        catch (Exception ex) { Console.WriteLine($"   异常 {ex.Message}"); break; }
                        if (!rT9.Ok) { Console.WriteLine($"   ✗ {rT9.Message}"); break; }
                        lastT9 = rT9;

                        var dt = rT9.Segments.Select(s => s.RootDeltaK).ToArray();
                        if (dt.All(d => d > 0 && d <= 10.0)) break;

                        var nIns = (double[])insT9.Clone();
                        var nTab = (double[])tabT9.Clone();
                        bool moved = false;
                        for (int j = 0; j < 4; j++)
                        {
                            double e = 0; int c = 0;
                            if (j - 1 >= 0 && j - 1 < dt.Length) { e += dt[j - 1] - targetT9; c++; }
                            if (j < dt.Length) { e += dt[j] - targetT9; c++; }
                            if (c == 0) continue;
                            e /= c;
                            if (Math.Abs(e) < 1.0) continue;

                            double want = Math.Clamp(insT9[j] + 0.06 * e, 0.5, 30.0);
                            if (Math.Abs(want - insT9[j]) > 1e-9) { nIns[j] = want; moved = true; continue; }

                            double step = Math.Clamp(0.004 * Math.Abs(e), 0.01, 0.15);
                            double t2 = e < 0 ? tabT9[j] * (1 + step) : tabT9[j] * (1 - step);
                            nTab[j] = Math.Clamp(t2, 0.3, 4.0);
                            if (Math.Abs(nTab[j] - tabT9[j]) > 1e-9) moved = true;
                        }
                        if (!moved) break;
                        insT9 = nIns; tabT9 = nTab;
                    }
                    if (lastT9 == null) { Console.WriteLine("   ✗ 无解"); Console.WriteLine(); continue; }

                    var lcT9b = MakeT9(insT9, tabT9); lcT9b.CheckRamp = true;
                    try
                    {
                        var rc = LineRunner.Run(lcT9b);
                        if (rc.Ok) lastT9 = rc;
                    }
                    catch { /* 保留迭代末解 */ }

                    var dtF = lastT9.Segments.Select(s => s.RootDeltaK).ToArray();
                    double mT = lastT9.Segments.Sum(s => s.MassG) + lastT9.Flanges.Sum(f => f.MassG);
                    Console.WriteLine($"   舌厚 {string.Join("/", tabT9.Select(v => v.ToString("0.00")))}" +
                                      $"　保温 {string.Join("/", insT9.Select(v => v.ToString("0.0")))}");
                    Console.WriteLine($"   管根ΔT {string.Join(" / ", dtF.Select(v => v.ToString("+0.0;−0.0")))} K" +
                                      $"　合计 **{mT:0} g**");
                    foreach (var ck in lastT9.Checks.Where(x => x.Kind == CheckKind.HardSafety))
                        Console.WriteLine($"   {(ck.Ok ? "✓" : "✗")} {ck.Name,-22}{ck.Actual,9:+0.00;−0.00} / {ck.Limit,6:0.00}  {ck.Where}");
                    Console.WriteLine($"   法兰 J_max {lastT9.Flanges.Max(f => f.JMaxAPerMm2):0.0} A/mm²" +
                                      $"　舌端温 {string.Join("/", lastT9.Flanges.Select(f => f.TTabEndC.ToString("0")))} °C");
                    Console.WriteLine();
                }

                Console.WriteLine("★ 若双舌片把 ② 与 ②″ 同时压到 ≤0 而铂重基本不变，");
                Console.WriteLine("  它就是本方案的最后一块拼图 —— 代价只是每片两根铜排。");
                return;
            }

            // --cli --hotspot   ★ 直接量「峰值在哪」——两次靠猜都猜错之后加的
            //
            // 先猜峰值在包保温的舌片上 → 加 ②″ 分区判据，结果分区值一模一样，猜错；
            // 再猜是等宽舌片与圆盘的凹尖角 → 加过渡圆角，结果**逐位相同**（圆角只改
            // 0.25 mm 的轮廓，落在 2 mm 网格之下），又猜错。
            // ⇒ 不再猜：把 J_max / T_max 的**坐标、半径、厚度、所属区**直接打出来。
            // 2026-08-14 第三次猜错：以为双舌片能靠对称消掉峰值，实算 ② 反而从 +2.85 涨到 +9.24。
            // ⇒ 本条改成量**定案方案本身**（盘舌等厚 1.80、管壁 0.6），并新增**沿管孔一圈的角向剖面**
            //   ——「电流从单侧绕过管孔」这个说法到底成不成立，只有角向剖面能证伪。
            if (args.Contains("--hotspot"))
            {
                double wallH = p.WeldMinThicknessMm, holeH = wallH + 25.0;
                double discH = 30.0;
                var pH = SegmentSolver.Clone(p);
                pH.Layer1.ThicknessMm = 10.0; pH.Layer1.Enabled = true;
                pH.WallMinMm = wallH;
                pH.FlangeInsulThickMm = 20; pH.FlangeInsulated = true;
                pH.BusbarClampLengthMm = 40; pH.BusbarClampTempC = 300;

                // ★ 必须对着**当前**定案构型量，不是旧的。
                //   本轮已经因此白做过两次：一次拿 300 °C/基板 1.23 那版的峰值位置去设计
                //   450 °C 那版；一次拿入口片当靶而整线上最差的是 HC2|HC3。
                //   下面这组 = `--final2` 压接 450 档的收敛解（舌长 90、半宽 15、两级渐变环）。
                double[] tabH = { 1.37, 2.08, 1.86, 1.04 }, insH = { 18.2, 0.8, 0.9, 7.5 };
                double[] stepRH = { 30.0, 36.0 }, stepTH = { 2.4, 1.7 };
                double discFloorH = WeldDistortion.ForPt(1.0, kb: 0.43).SlopePerB
                                    * (discH - 26.0) * p.WeldSafetyFactor;
                pH.BusbarClampTempC = 450;
                FlangePlate MkH(int j)
                {
                    double td = Math.Max(tabH[j], discFloorH);
                    return new FlangePlate
                    {
                        DiscRadiusMm = discH, HoleRadiusMm = holeH,
                        TabEndXMm = -90.0, TabEndHalfWidthMm = 15.0,
                        ThicknessMm = td,
                        DiscStepRadiiMm = stepRH,
                        DiscStepThicknessMm = stepTH.Select(v => Math.Max(v, td)).ToArray(),
                        TabThicknessMm = double.NaN,
                        InsulBoundaryXMm = double.NaN, TabInsulThickMm = insH[j],
                        TabParallel = true, TabFilletMm = 3.0,
                        WeldFilletLegMm = Math.Max(td, wallH)
                    };
                }
                var lcH = new LineCase
                {
                    Base = SegmentSolver.Clone(pH), WallMm = wallH,
                    UseMeasuredCurrent = false, CheckRamp = false,
                    SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                    FlangePlates = new[] { MkH(0), MkH(1), MkH(2), MkH(3) },
                    ClampTempC = new[] { 450.0, 450.0, 450.0, 450.0 }
                };
                var rH = LineRunner.Run(lcH);
                if (!rH.Ok) { Console.WriteLine("✗ " + rH.Message); return; }

                // 取 ② 最差的那一片来解剖
                int jw = 0;
                for (int j = 1; j < rH.Flanges.Length; j++)
                    if (rH.Flanges[j].TMaxC - rH.Flanges[j].TRootC
                        > rH.Flanges[jw].TMaxC - rH.Flanges[jw].TRootC) jw = j;
                var fw = rH.Flanges[jw];

                var gH = MkH(jw);
                var mH = FlangeMesher.Build(gH, 0, 2.0, 11.0, 45.0, 40.0);
                double xtH = gH.Tangent().X;
                var scH = ShellCurrent.Solve(mH, fw.CurrentA,
                              Materials.PtResistivity(fw.TRootC) * 1e3, fw.TRootC);
                var thH = ShellThermal.Solve(mH, scH.JMagAPerMm2, pH, fw.TRootC,
                              gH.InsulBoundaryXResolved, tabBoundaryX: xtH,
                              tabInsulThickMm: gH.TabInsulThickMm);

                Console.WriteLine($"=== 峰值位置实测（② 最差的片：{fw.Name}）===");
                Console.WriteLine($"I={fw.CurrentA:0} A　管根 {fw.TRootC:0.0} °C　" +
                                  $"盘Ø{2 * discH:0}／等宽舌 90×30／**盘舌等厚 {gH.ThicknessMm:0.00}**／" +
                                  $"舌保温 {insH[jw]:0.0}／焊脚 {gH.WeldFilletLegMm:0.00}／切点 x={xtH:0.00}");
                Console.WriteLine($"整线判定 ② = {fw.TMaxC - fw.TRootC:+0.00;−0.00} K");
                Console.WriteLine();

                void Report(string what, int idx)
                {
                    var c0 = mH.Centroid[idx];
                    double r0 = Math.Sqrt(c0.X * c0.X + c0.Z * c0.Z);
                    double ang = Math.Atan2(c0.Z, c0.X) * 180 / Math.PI;
                    Console.WriteLine($"{what}：x={c0.X,8:0.00}  z={c0.Z,8:0.00}  r={r0,7:0.00}" +
                        $"  θ={ang,7:0}°  厚={mH.Thickness[idx],6:0.000}  J={scH.JMagAPerMm2[idx],7:0.0}" +
                        $"  T={thH.T[idx],7:0.0}  区={(c0.X < xtH ? "舌片" : "圆盘")}" +
                        $"  {(Math.Abs(r0 - gH.HoleRadiusMm) < 3 ? "★贴管孔" : "")}");
                }
                int iJ = 0, iT = 0;
                for (int i = 1; i < mH.CellCount; i++)
                {
                    if (scH.JMagAPerMm2[i] > scH.JMagAPerMm2[iJ]) iJ = i;
                    if (thH.T[i] > thH.T[iT]) iT = i;
                }
                Report("J 峰值", iJ);
                Report("T 峰值", iT);
                Console.WriteLine($"管根定温 {fw.TRootC:0.0} °C ⇒ T 峰值超出 " +
                                  $"{thH.T[iT] - fw.TRootC:+0.00;−0.00} K");
                Console.WriteLine();

                // ★ 新增：沿管孔一圈（r ≈ 孔半径+2）的角向剖面。
                //   θ=180° 是舌片方向，θ=0° 是背对舌片那一侧。
                //   「电流从单侧绕过管孔堆成峰值」若成立，J 必须在 180° 附近显著高于 0°。
                Console.WriteLine("── 沿管孔一圈的角向剖面（θ=180° 朝舌片，θ=0° 背对舌片）");
                Console.WriteLine($"{"θ°",7}{"x",8}{"z",8}{"厚度",8}{"J",8}{"T",9}{"T−管根",9}");
                for (double th = 0; th <= 180; th += 15)
                {
                    double rq = gH.HoleRadiusMm + 2.0;
                    double xq = rq * Math.Cos(th * Math.PI / 180), zq = rq * Math.Sin(th * Math.PI / 180);
                    int best = -1; double bd = 1e9;
                    for (int i = 0; i < mH.CellCount; i++)
                    {
                        double d = Math.Abs(mH.Centroid[i].X - xq) + Math.Abs(mH.Centroid[i].Z - zq);
                        if (d < bd) { bd = d; best = i; }
                    }
                    if (best < 0 || bd > 5) continue;
                    Console.WriteLine($"{th,7:0}{mH.Centroid[best].X,8:0.0}{mH.Centroid[best].Z,8:0.0}" +
                        $"{mH.Thickness[best],8:0.000}{scH.JMagAPerMm2[best],8:0.0}" +
                        $"{thH.T[best],9:0.0}{thH.T[best] - fw.TRootC,9:+0.00;−0.00}");
                }
                Console.WriteLine();

                Console.WriteLine("── 沿舌片中线 (z≈0) 的厚度与 J 剖面");
                Console.WriteLine($"{"x mm",8}{"半宽",8}{"厚度",8}{"J",8}{"T",9}{"T−管根",9}");
                for (double xq = 30; xq >= -90; xq -= 4)
                {
                    int best = -1; double bd = 1e9;
                    for (int i = 0; i < mH.CellCount; i++)
                    {
                        double d = Math.Abs(mH.Centroid[i].X - xq) + Math.Abs(mH.Centroid[i].Z) * 0.5;
                        if (d < bd) { bd = d; best = i; }
                    }
                    if (best < 0 || bd > 6) continue;
                    Console.WriteLine($"{mH.Centroid[best].X,8:0.0}{gH.HalfWidth(mH.Centroid[best].X),8:0.0}" +
                        $"{mH.Thickness[best],8:0.000}{scH.JMagAPerMm2[best],8:0.0}" +
                        $"{thH.T[best],9:0.0}{thH.T[best] - fw.TRootC,9:+0.00;−0.00}");
                }
                Console.WriteLine();
                Console.WriteLine("★ 判读：");
                Console.WriteLine("  · 角向剖面若 180° 与 0° 的 J 差不多 ⇒ **「单侧绕流」这个说法不成立**，");
                Console.WriteLine("    双舌片救不了 ② 就有了解释（实算正是如此）。");
                Console.WriteLine("  · T 峰值若落在舌片中段而不是管孔附近 ⇒ 是**一维杆的分布发热**问题：");
                Console.WriteLine("    两端定温、中间发热的杆必然中间最热，与绕流无关 ⇒ 对策是改**沿程发热分布**，");
                Console.WriteLine("    即沿舌片渐变厚度或分段保温，而不是改平面形状。");
                return;
            }

            // --cli --weldmin   ★★★★ 焊接变形定的工艺下界（用户 2026-08-14：「这应该是第一步」）
            //
            // 见 WeldDistortion 的类注释：屈曲那一半能算（且 E 与 ρ 都约掉，只剩热学量与
            // 三个工艺系数），烧穿那一半算不出、必须现场给。先在钢上验公式，再用于铂。
            if (args.Contains("--weldmin"))
            {
                Console.WriteLine("=== 焊接变形定的最小厚度 ===");
                Console.WriteLine();
                Console.WriteLine("t_min = 12(1−ν²)·C·β·α·(c̄ΔT_m + L_f)·b / (k_b·π²·c̄·η_melt)");
                Console.WriteLine("  ★ E 与 ρ 在推导中约掉 ⇒ 只剩热学物性与三个工艺系数");
                Console.WriteLine("  ★ **正比于无支撑宽度 b** ⇒ 它给的是 t/b 的下限，不是「一个厚度」");
                Console.WriteLine($"  系数：C={WeldDistortion.TendonC}（收缩力，钢上标定）　" +
                                  $"β={WeldDistortion.BeadWidthRatio}（焊道宽/板厚）　" +
                                  $"η_melt={WeldDistortion.MeltEfficiency}　k_b={WeldDistortion.PlateBucklingK}");
                Console.WriteLine();

                // ── ① 先在钢上验证：公式必须先复现车间常识
                double sSteel = WeldDistortion.Slope(12e-6, 600, 270e3, 1480, 0.30);
                double sPt = WeldDistortion.ForPt(1.0).SlopePerB;
                Console.WriteLine("── ① 可信度锚点：同一套公式代入低碳钢");
                Console.WriteLine($"   钢  t_min/b = {sSteel:0.0000}  ⇒ 宽 300 mm 板需 {sSteel * 300:0.00} mm，" +
                                  "宽 1000 mm 需 " + (sSteel * 1000).ToString("0.0") + " mm");
                Console.WriteLine("   车间常识：薄钢板约 2–3 mm 以下、大面积焊后必鼓曲 ⇒ **量级对上**");
                Console.WriteLine($"   铂  t_min/b = {sPt:0.0000}（与钢接近：铂 α 小但比热也小，两者抵消）");
                Console.WriteLine();

                // ── ② 本装配的每一条焊缝。k_b 是最大不确定源（简支 4.0 vs 一边自由 0.43，
                //     差 9.3 倍），故不给单值，给区间；安全系数按用户指示加在结果上。
                double sf = p.WeldSafetyFactor;
                double sPtFree = WeldDistortion.ForPt(1.0, kb: 0.43).SlopePerB;
                Console.WriteLine($"── ② 本装配各焊缝（b = 无支撑宽度；已乘安全系数 {sf:0.0}）");
                Console.WriteLine("   两列分别是板边**简支**(k_b=4.0) 与**一边自由**(k_b=0.43) 两种极端；");
                Console.WriteLine("   法兰盘内边焊在管上、外边自由 ⇒ 以**右列**为准。");
                Console.WriteLine($"{"焊缝",-20}{"b 取法",-24}{"b mm",7}{"简支 t_min",11}{"自由边 t_min",13}  说明");

                void WRow(string nm, string how, double b, string note)
                    => Console.WriteLine($"{nm,-20}{how,-24}{b,7:0.0}{sPt * b * sf,11:0.00}" +
                                         $"{sPtFree * b * sf,13:0.00}  {note}");

                // 用户 2026-08-14：**焊接处是铂金管与圆盘法兰的内孔** —— 全装配只此一条环缝。
                // 它连接两个厚度不同的件，两边各有各的失效方式：
                //   · 管侧：圆筒，曲率把屈曲半波长压在 2.4√(Rt) 内 ⇒ 结构上不会鼓曲
                //   · 盘侧：内边被焊住、外边自由的**环**，环缝的周向收缩把环往里箍 ⇒ 起皱/翘锥
                // 故下表只算这一条，两侧分开列。
                double tubeR = 25.0;
                Console.WriteLine("   （全装配只有这一条焊缝：管 Ø52 外壁 ↔ 圆盘内孔）");
                double shellRatio = WeldDistortion.PtShellBucklingRatio();
                Console.WriteLine($"   管侧（圆筒）：走另一条判据 —— 轴压屈曲，推导中 **t 与 R 同时约掉**，");
                Console.WriteLine($"     只剩「实际收缩力/临界值 = {shellRatio:0.0000}」" +
                                  (shellRatio < 1
                                   ? $" < 1 ⇒ **任何壁厚都不会被环缝压屈**（差 {1 / shellRatio:0} 倍）"
                                   : " ≥ 1 ⇒ 任何壁厚都会屈曲"));
                Console.WriteLine("     ⇒ 管壁下界不可能由变形定，只能由烧穿定。");
                Console.WriteLine();
                foreach (var (nm, ro) in new[] { ("盘侧 Ø120", 60.0), ("盘侧 Ø72", 36.0),
                                                 ("盘侧 Ø60", 30.0), ("盘侧 Ø56", 28.0) })
                    WRow(nm, "环宽 = 外半径−孔半径", ro - 26.0, "外边自由 ⇒ 看右列");
                Console.WriteLine();
                Console.WriteLine("   ★ 舌片与圆盘是**同一张板切出来的**，中间没有焊缝 ⇒ 舌片厚度完全不受本判据约束，");
                Console.WriteLine("     它只由电流密度与局部热稳定定（--leadbound）。这一条把两个自由度彻底解耦。");
                Console.WriteLine();

                // ⚠⚠ 此处曾用「现役 Ø120×2.0 焊得出来」反查，把 k_b 夹到 0.0588 并据此
                //    否掉最保守的取法。**用户 2026-08-14 澄清：那组尺寸是随手给的一组，
                //    不是被现场验证过的产品** ⇒ 该反查不成立，已撤回。
                //    公式本身仍然有效（钢上验证过，见 ①），**只是边界系数 k_b 现在无锚点**，
                //    只能取保守端。这一段留着是为了记住：**别再拿那组尺寸当实测。**
                double slopeUse = sPtFree;
                Console.WriteLine("── ②′ 关于 k_b 的锚点：**没有**（2026-08-14 撤回）");
                Console.WriteLine("   曾用「现役 Ø120×2.0 焊得出来」反查得 t_min/b ≤ 0.0588，据此否掉自由边取法。");
                Console.WriteLine("   用户澄清那是**随手给的一组尺寸、不是验证过的产品** ⇒ 该证据撤回。");
                Console.WriteLine($"   ⇒ 只剩理论两端：简支 k_b=4.0 给 {sPt:0.0000}，一边自由 k_b=0.43 给 {sPtFree:0.0000}；");
                Console.WriteLine($"     法兰是「内边焊在管上、外边自由」⇒ **取保守端 {slopeUse:0.0000}**。");
                Console.WriteLine("   要把它收窄，只能靠**试焊一片**：焊一片已知环宽的板，看它鼓不鼓。");
                Console.WriteLine();
                Console.WriteLine($"── ②″ 按夹住后的斜率 {slopeUse:0.0000}／mm，各盘径的屈曲下界（含安全系数 {sf:0.0}）");
                Console.WriteLine($"{"圆盘",-14}{"环宽 b mm",12}{"屈曲下界 mm",14}  与烧穿下界比");
                foreach (var (nm, ro) in new[] { ("Ø120", 60.0), ("Ø72", 36.0), ("Ø60", 30.0), ("Ø56", 28.0) })
                {
                    double bb = ro - 26.0, tb = slopeUse * bb * sf;
                    Console.WriteLine($"{nm,-14}{bb,12:0.0}{tb,14:0.00}  " +
                        (tb > 0.5 ? "★ 屈曲控制（比手工 TIG 的 0.5 还严）" : "烧穿控制"));
                }
                Console.WriteLine();

                // ── ③ 另一条下界：烧穿
                Console.WriteLine("── ③ 另一条下界：烧穿 / 熔池失控（**算不出，必须现场给**）");
                Console.WriteLine($"   熔池毛细宽度 √(2γ/ρg) = {WeldDistortion.CapillaryWidthMm():0.0} mm" +
                                  "  ⇒ 对 1 mm 级铂板不是限制，掉熔池不是这里的机理");
                Console.WriteLine("   真正的烧穿下界取决于焊接方法与热输入控制精度，差一个量级：");
                Console.WriteLine("     手工 TIG   约 0.5 mm 以下显著变难（电弧力与热输入波动）");
                Console.WriteLine("     自动 TIG   约 0.3 mm");
                Console.WriteLine("     激光 / 电阻缝焊  约 0.1 mm 量级");
                Console.WriteLine("   ⚠ 这三个数是**行业常规，不是本项目实测**，须向焊接方确认。");
                Console.WriteLine();

                // ── ④ 结论：两条下界取大，且**必须按盘径分别给** —— 屈曲下界正比于环宽
                Console.WriteLine("── ④ 结论：焊接下界 = max(屈曲, 烧穿)，且**随圆盘直径变**");
                Console.WriteLine();
                Console.WriteLine($"{"件",-16}{"屈曲",10}{"烧穿(手工TIG)",15}{"取大 ⇒ 下界",14}  控制机理");
                double tBurn = 0.5;
                Console.WriteLine($"{"管（圆筒侧）",-16}{"不屈曲",10}{tBurn,15:0.00}" +
                                  $"{tBurn,14:0.00}  轴压比值 {shellRatio:0.000}≪1 ⇒ 烧穿控制");
                foreach (var (nm, ro) in new[] { ("圆盘 Ø120", 60.0), ("圆盘 Ø72", 36.0), ("圆盘 Ø60", 30.0) })
                {
                    double tb = slopeUse * (ro - 26.0) * sf;
                    Console.WriteLine($"{nm,-16}{tb,10:0.00}{tBurn,15:0.00}{Math.Max(tb, tBurn),14:0.00}  " +
                                      (tb > tBurn ? "★ 屈曲控制" : "烧穿控制"));
                }
                Console.WriteLine($"{"舌片",-16}{"—",10}{"—",15}{"不受限",14}  与盘同板切出、无焊缝");
                Console.WriteLine();
                Console.WriteLine("★ 三条可执行的结论：");
                Console.WriteLine("  ① **缩小圆盘直径同时放松焊接下界** —— 环宽小了，屈曲下界跟着线性下来。");
                Console.WriteLine("     这与省铂、与端片热平衡（缩盘减少死区散热）**三者同向**，是本问题里少有的。");
                Console.WriteLine("  ② **圆盘的下界与焊接方法无关** —— Ø60 的屈曲下界 0.55 已高于手工 TIG 的烧穿 0.5。");
                Console.WriteLine("     在手工 TIG 档（管壁 0.6）圆盘实际厚度被电热约束顶到 1.0–2.0 mm，");
                Console.WriteLine("     两条焊接下界对圆盘**都不起作用**；管壁再薄下去圆盘才会被 0.55 顶住。");
                Console.WriteLine("     ⇒ 焊接方法只经**管壁**一条路影响铂重（管侧不屈曲，只可能烧穿）。");
                Console.WriteLine("     ⚠ 但 `--weldvalue` 实算表明**这条路也走不通**：管壁 0.30 时空管升不到温，");
                Console.WriteLine("       真正的管壁下界是**升温能力**（落在 0.30–0.50），不是焊接 ⇒ 见 --wallfloor。");
                Console.WriteLine("  ③ **舌片不受任何焊接下界约束**（同板切出），它只由 --leadbound 的两条电热约束定。");
                Console.WriteLine();
                Console.WriteLine($"程序当前取 WeldMinThicknessMm = {p.WeldMinThicknessMm:0.00} mm。");
                Console.WriteLine("用户 2026-08-14 已答：**目前是手工焊接** ⇒ 管壁烧穿下界约 0.5，程序取 0.6 留一档余量。");
                Console.WriteLine("⚠ 仍待现场确认（§6 待补①）：");
                Console.WriteLine("   · 手工 TIG 在**铂**上的实际烧穿下界（0.5 是行业常规值，非本项目实测）；");
                Console.WriteLine("   · 若愿意换自动 TIG 或激光焊，省铂幅度见 `--weldvalue`。");
                return;
            }

            // --cli --leadbound   ★★★★ 引线可行性的闭式判据：TCR·ΔT < 4
            //
            // 本轮把「端片能不能自给」一路逼到只剩两条约束，两条都只作用在**舌片**上，
            // 且舌片必须包保温（否则裸露散热把它拖成净抽热）。包了保温的舌片就是一根
            // 「两端定温、通电、绝热」的杆，于是两条约束都能写成闭式：
            //
            //   ① 局部热失稳（上界）：J ≤ J_stab = (1/L)·√(k/(ρe·TCR))，L = 自由段/2
            //      发热 P = I²ρe·ℓ/A = I·J·ρe·ℓ，代入 J=J_stab、L=ℓ/2：
            //        **P_max = 2·I·√(ρe·k/TCR)**      ← ℓ 与 A 全部约掉
            //
            //   ② 引线导热漏（下界）：**注意这里有个因子 2，我第一版漏了**。
            //      带分布发热 p 的杆，两端定温（热端 T_h = 管根、冷端 T_c = 压接）：
            //        T′(0) = −ΔT/ℓ + pℓ/(2kA)  ⇒ 从热端流进杆的热 = k·A·ΔT/ℓ − P/2
            //      令它 =0（不抽管）得 **P = 2·k·A·ΔT/ℓ = 2Q**，不是 P = Q ——
            //      发的热有一半往热端走、一半往冷端走，所以要盖住漏热得发两倍。
            //      仍有 P·Q = I²·ρe·k·ΔT（**与几何无关**，ℓ/A 在两式里正好抵消）：
            //        **P_min = I·√(2·ρe·k·ΔT)**
            //      （ρe·k = 洛伦兹数×T̄ 即维德曼–弗兰兹，故也等于 I·√(2L₀T̄ΔT)）
            //
            //   ⇒ 可行 ⟺ P_max ≥ P_min ⟺ **√(2/(TCR·ΔT)) ≥ 1 ⟺ TCR·ΔT ≤ 2**
            //
            // 这个比值**既不含几何、也不含电流**：舌片怎么改形状、电流多大，都不改变它。
            // 它一次性回答了 §4.3b 的「端片是不是结构性死路」—— 只要 TCR·ΔT < 4 就不是。
            if (args.Contains("--leadbound"))
            {
                Console.WriteLine("=== 引线（舌片）可行性的闭式判据 ===");
                Console.WriteLine();
                Console.WriteLine("包保温的舌片 = 一根「两端定温、通电、绝热」的杆。两条约束各给一个发热界：");
                Console.WriteLine("  局部稳定上界  P_max = 2·I·√(ρe·k/TCR)      ← 舌长与截面全部约掉");
                Console.WriteLine("  导热漏下界    P_min = I·√(2·ρe·k·ΔT)       ← 同样与几何无关");
                Console.WriteLine("    （因子 2：发的热一半往热端一半往冷端，零抽管要 P = 2·kAΔT/ℓ）");
                Console.WriteLine("  ⇒ 裕度 = P_max/P_min = **√(2/(TCR·ΔT))**，既不含几何也不含电流");
                Console.WriteLine("  ⇒ **可行 ⟺ TCR·ΔT ≤ 2**");
                Console.WriteLine();
                Console.WriteLine($"{"舌片温度°C",11}{"ρe Ω·m",12}{"k W/mK",9}{"TCR 1/K",11}" +
                                  $"{"夹持°C",8}{"ΔT K",7}{"TCR·ΔT",9}{"裕度",7}  判定");
                foreach (double tTab in new[] { 900.0, 1000.0, 1100.0, 1150.0, 1250.0, 1400.0 })
                    foreach (double tc in new[] { 25.0, 300.0, 600.0 })
                    {
                        double rho = Materials.PtResistivity(tTab);
                        double kk = Materials.PtThermalK(tTab);
                        double tcr = Materials.PtTcr(tTab);
                        double dT = tTab - tc;
                        if (dT <= 0) continue;
                        double prod = tcr * dT;
                        double marg = Math.Sqrt(2.0 / prod);
                        Console.WriteLine($"{tTab,11:0}{rho,12:E3}{kk,9:0.0}{tcr,11:E3}" +
                            $"{tc,8:0}{dT,7:0}{prod,9:0.000}{marg,7:0.00}  " +
                            (marg >= 1.0 ? "✓ 可行" : "✗ 无解"));
                    }
                Console.WriteLine();
                Console.WriteLine("★ 结论：纯铂在 900–1400 °C、夹持 25–600 °C 的**整个范围内** TCR·ΔT ≈ 0.21–0.62，");
                Console.WriteLine("  裕度 1.8–3.1 ⇒ **端片不是结构性死路**。§4.3b 的「两条约束没有交集」被推翻，");
                Console.WriteLine("  那个结论来自「舌片只能裸露或全包」的二值假设，以及漏掉压接端锚点的 J_stab。");
                Console.WriteLine("  ⚠ 但裕度只有约 2 倍，而下面三项实际损耗要吃掉它的大部分：");
                Console.WriteLine("    · 梯形舌片：导热按平均截面、J 按最窄截面 ⇒ 两头吃亏，实测约 1.25 倍");
                Console.WriteLine("    · 角点电流集中：J 峰值/名义约 1.39 倍");
                Console.WriteLine("    · 圆盘远侧电流死区：只散热不发热");
                Console.WriteLine("  ⇒ **必须把舌片改成等宽（矩形）**，否则闭式的裕度落不到实处。");
                Console.WriteLine();
                Console.WriteLine("── 由闭式反推的设计点（P = P_min 时，裕度自动 = 上面那一列）");
                Console.WriteLine("   A/ℓ = I·√(ρe/(2k·ΔT))　⇒ 截面随自由段长线性走，质量 ∝ ℓ² ⇒ **短舌更省铂**");
                Console.WriteLine("   末列「裕度」应与上表同一温度那一行一致 —— 不一致就是这里算错了。");
                Console.WriteLine($"{"电流A",7}{"舌温°C",8}{"夹持°C",8}{"P_min W",9}{"A/ℓ mm²/mm",13}" +
                                  $"{"自由段40时 A",13}{"J A/mm²",10}{"J_stab",9}{"裕度",7}");
                foreach (double ia in new[] { 687.0, 1105.0 })
                    foreach (double tc in new[] { 25.0, 300.0 })
                    {
                        double tTab = 1150.0;
                        double rho = Materials.PtResistivity(tTab), kk = Materials.PtThermalK(tTab);
                        double tcr = Materials.PtTcr(tTab), dT = tTab - tc;
                        double pMin = ia * Math.Sqrt(2 * rho * kk * dT);
                        // P = 2kAΔT/ℓ 与 P = I²ρeℓ/A 联立 ⇒ A = I·ℓ·√(ρe/(2kΔT))
                        // （因子 2 在**分母**：要发两倍的热，就得让截面更小、电阻更大）
                        double aOverL = ia * Math.Sqrt(rho / (2 * kk * dT)) * 1e3;   // m→mm²/mm
                        double lFree = 40.0, aMm2 = aOverL * lFree;
                        double j = ia / aMm2;
                        double jStab = 1e-6 / (lFree * 0.5 * 1e-3) * Math.Sqrt(kk / (rho * tcr));
                        Console.WriteLine($"{ia,7:0}{tTab,8:0}{tc,8:0}{pMin,9:0}{aOverL,13:0.000}" +
                            $"{aMm2,13:0.0}{j,10:0.00}{jStab,9:0.00}{jStab / j,7:0.00}");
                    }
                Console.WriteLine();
                Console.WriteLine("★ 用法：这是**必要且充分**的一次性判断（在「舌片包保温、两端定温」这个理想化下）。");
                Console.WriteLine("  实际还要加上圆盘散热与压接段不发热两项修正 ⇒ 仍须 --endsolve 数值复核。");
                return;
            }

            // --cli --endsolve   ★★★ 端片定解：对每个(舌长,舌厚)反解「使抽管=0」的舌片保温厚度
            //
            // 为什么这么组织（三条都是本轮实算逼出来的）：
            //  ① **压接段在电学上是等位体，整段不发热**（ShellCurrent 把 TagTabEnd 全钉 V=1，
            //     物理上对：铜排比铂导电 25 倍、厚 10 倍，那 40 mm 就是被短接的）。
            //     ⇒ 舌片真正发热的长度 = 舌长 − 压接长。90 mm 舌片扣掉 40 只剩 43 ——
            //     这正是闭式下界要 106 W 而实算只有 65 W 的全部原因，**不是模型错**。
            //     ⇒ 舌长是第一杠杆，而此前一路在**缩短**它省铂（§4.3d 甚至缩到 50）。
            //  ② 舌片保温厚度是让「抽管」穿零的连续旋钮（--endplate ⑥），故把它反解掉，
            //     剩下 (舌长, 舌厚) 两个真正花铂的量做 Pareto。
            //  ③ 夹持回到 300 °C 定温 —— 但**必须补上此前一直缺的那一步**：
            //     由 QClamp 反推所需铜排热导 G，再由维德曼–弗兰兹算它的电学代价，
            //     确认这根铜排做得出来。定温边界只有配上这一步才不是「假设结论」。
            if (args.Contains("--endsolve"))
            {
                const double Lorenz = 2.44e-8;
                double holeS9 = 0.6 + 25.0, discS9 = 30.0, halfWS9 = 20.0;
                double wallS9 = 0.6;                        // = 焊接工艺下界（用户 2026-08-14）
                double tRootS9 = 1150.0, clampS9 = 300.0, sinkS9 = 25.0, clampLenS9 = 40.0;

                var pS9 = SegmentSolver.Clone(p);
                pS9.Layer1.ThicknessMm = 10.0; pS9.Layer1.Enabled = true;
                pS9.WallMinMm = wallS9;
                pS9.FlangeInsulThickMm = 20; pS9.FlangeInsulated = true;
                pS9.BusbarClampLengthMm = clampLenS9;
                pS9.BusbarClampTempC = clampS9;

                // 圆盘厚度取焊接下界（Ø60 环宽 4 mm ⇒ 屈曲 0.47，烧穿 0.5 ⇒ 取 0.5，见 --weldmin）。
                // ⚠ 这一节仍按「盘薄舌厚」的阶梯板筛选，那意味着要机加工或拼板；
                //   若按「同板切出」则盘舌必须等厚 —— 见 --linefinal 的等厚方案对比。
                double tDiscS9 = 0.5;
                FlangePlate MkS9(double tabL, double tTab, double ins, double halfW) => new()
                {
                    DiscRadiusMm = discS9, HoleRadiusMm = holeS9,
                    TabEndXMm = -tabL, TabEndHalfWidthMm = halfW,
                    ThicknessMm = tDiscS9, ThickenedMm = tDiscS9, TabThicknessMm = tTab,
                    InsulBoundaryXMm = double.NaN, TabInsulThickMm = ins,
                    TabParallel = true          // 梯形在两条约束上同时吃亏，见 FlangePlate.TabParallel
                };

                (ShellThermalResult Th, double JTab, double JStab, double MassG)
                    RunS9(double tabL, double tTab, double ins, double iA, double halfW)
                {
                    var g = MkS9(tabL, tTab, ins, halfW);
                    var mesh = FlangeMesher.Build(g, 0, 2.0, 11.0, 45.0, clampLenS9);
                    var sc = ShellCurrent.Solve(mesh, iA,
                                Materials.PtResistivity(tRootS9) * 1e3, tRootS9);
                    var p2 = SegmentSolver.Clone(pS9);
                    p2.TSetC = tRootS9;
                    double xt = g.Tangent().X;
                    var th = ShellThermal.Solve(mesh, sc.JMagAPerMm2, p2, tRootS9,
                                g.InsulBoundaryXResolved, tabBoundaryX: xt,
                                tabInsulThickMm: g.TabInsulThickMm);
                    double jT = 0, tAtJ = 0, thAtJ = tTab;
                    for (int i = 0; i < mesh.CellCount; i++)
                        if (mesh.Centroid[i].X < xt && sc.JMagAPerMm2[i] > jT)
                        { jT = sc.JMagAPerMm2[i]; tAtJ = th.T[i]; thAtJ = mesh.Thickness[i]; }
                    double lat = LocalStability.TabHalfSpanMm(tabL, clampLenS9, xt);
                    double js = LocalStability.Check(p2, tAtJ, jT, thAtJ,
                                    double.IsNaN(ins) ? 0 : ins, lat).JStab;
                    return (th, jT, js, mesh.VolumeMm3 * Materials.PtDensity * 1e-6);
                }

                void ScanS9(string title, double iA)
                {
                    Console.WriteLine(title);
                    Console.WriteLine($"{"舌长",6}{"末宽",6}{"舌厚",6}{"截面",7}{"保温*",7}{"抽管",7}" +
                                      $"{"盘净",6}{"舌净",6}{"J舌",6}{"J稳",6}" +
                                      $"{"裕度",6}{"峰温",6}{"铜排W",7}{"需G",7}{"铜排I²R",8}" +
                                      $"{"克/片",7}  判定");
                    // 闭式（--leadbound）：A/ℓ = I·√(ρe/(2kΔT))、质量 ∝ ℓ² ⇒ **短舌更省铂**。
                    // 端片 687 A / 夹 300 时 A/ℓ = 1.25 mm²/mm ⇒ 自由段 40 时截面 50 mm²。
                    // 网格就铺在这个设计点周围；舌片已改**等宽**，梯形的两项损失被去掉。
                    foreach (double tabL in new[] { 90.0, 105.0, 120.0 })
                        foreach (double halfW in new[] { 15.0, 20.0, 25.0 })
                            foreach (double tTab in new[] { 0.9, 1.3, 1.8 })
                            {
                                // 「抽管」随保温厚度单调下降（--endplate ⑥ 实测）⇒ 二分反解零点。
                                // 下界取 1.0 而不是 0：包 0 mm 走外覆材料 ε=0.45，比裸铂 0.18 还散热，
                                // 那一段非单调（实测 裸 +73 → 包1.0 +100）。
                                double lo = 1.0, hi = 30.0;
                                double fLo, fHi;
                                try
                                {
                                    fLo = RunS9(tabL, tTab, lo, iA, halfW).Th.QFromTubeW;
                                    fHi = RunS9(tabL, tTab, hi, iA, halfW).Th.QFromTubeW;
                                }
                                catch (Exception ex)
                                { Console.WriteLine($"{tabL,6:0}{halfW * 2,6:0}{tTab,6:0.0}  异常 {ex.Message}"); continue; }

                                string flag = "";
                                double ins;
                                if (fLo < 0) { ins = lo; flag = "（裸区即倒灌）"; }
                                else if (fHi > 0) { ins = hi; flag = "（包满仍抽管）"; }
                                else
                                {
                                    for (int k = 0; k < 12; k++)
                                    {
                                        double mid = 0.5 * (lo + hi);
                                        if (RunS9(tabL, tTab, mid, iA, halfW).Th.QFromTubeW > 0) lo = mid;
                                        else hi = mid;
                                    }
                                    ins = 0.5 * (lo + hi);
                                }

                                var (th, jT, js, mg) = RunS9(tabL, tTab, ins, iA, halfW);
                                double gReq = th.QToClampW / Math.Max(1e-9, clampS9 - sinkS9);
                                double rBus = BusbarSizing.CuRho(150) * BusbarSizing.CuK / Math.Max(1e-9, gReq);
                                double margin = jT > 1e-9 && !double.IsNaN(js) ? js / jT : double.NaN;

                                string v = th.OverMelt ? "✗✗ 已熔"
                                         : flag != "" ? "✗ " + flag
                                         : Math.Abs(th.QFromTubeW) > 15 ? "✗ 未收敛到零点"
                                         : double.IsNaN(margin) ? "? J稳超范围"
                                         : margin < 1.0 ? $"✗ 局部失稳 {margin:0.00}"
                                         : th.OverFitRange ? "⚠ 外推"
                                         : $"✓ 可行 裕度{margin:0.00}";
                                Console.WriteLine(
                                    $"{tabL,6:0}{halfW * 2,6:0}{tTab,6:0.0}{halfW * 2 * tTab,7:0.0}{ins,7:0.0}" +
                                    $"{(th.QFromTubeW >= 0 ? "+" : "−") + Math.Abs(th.QFromTubeW).ToString("0"),7}" +
                                    $"{th.QGenDiscW - th.QLossDiscW,6:+0;−0}{th.QGenTabW - th.QLossTabW,6:+0;−0}" +
                                    $"{jT,6:0.0}{(double.IsNaN(js) ? -1 : js),6:0.0}" +
                                    $"{(double.IsNaN(margin) ? -1 : margin),6:0.00}{th.TMaxC,6:0}" +
                                    $"{th.QToClampW,7:0}{gReq,7:0.00}{iA * iA * rBus,8:0}" +
                                    $"{mg,7:0}  {v}");
                            }
                    Console.WriteLine();
                }

                Console.WriteLine("=== 端片定解：反解「抽管=0」的舌片保温厚度，(舌长,舌厚) 做 Pareto ===");
                Console.WriteLine($"盘Ø{2 * discS9:0}／舌末宽 {2 * halfWS9:0}／盘厚 0.4／圆盘包 20／" +
                                  $"压接 {clampLenS9:0} mm 夹 {clampS9:0} °C／定管根 {tRootS9:0} °C");
                Console.WriteLine("★ 压接段是等位体不发热 ⇒ **有效发热长度 = 舌长 − 压接长**");
                Console.WriteLine("「需G」= 该方案要求铜排的热导 W/K；「铜排I²R」是同一根铜排由 WF 定死的电学代价。");
                Console.WriteLine();
                ScanS9($"── 端片（I = {687:0} A，单段电流）", 687.0);
                ScanS9($"── 共用片（I = {1105:0} A，√3 倍）", 1105.0);

                Console.WriteLine("★ 读法：");
                Console.WriteLine("· 「✓ 可行」= 抽管≈0、局部稳定裕度>1、峰温在拟合区内。");
                Console.WriteLine("· 同为可行时取**克/片**最小的；再看「铜排I²R」是不是做得出来的铜排。");
                Console.WriteLine("· 「包满仍抽管」= 该(舌长,舌厚)发热不够，只能再加长舌片。");
                return;
            }

            // --cli --busbar2   ★★ 铜排热导边界 + 维德曼–弗兰兹下界
            //
            // 起因：--endplate ⑧ 找到了第一个两条约束同时过的格（末20×2.2 包14），
            // 但它用的是**自由端**——假设铜排完全不导热。而铜排必须导 687 A，
            // 金属的导电与导热被维德曼–弗兰兹定律锁在一起，**电流引线必然是热漏**。
            //
            // 对一根两端温差 ΔT、电阻 R、载流 I 的导体：
            //     焦耳热  P = I²R            导热漏  Q = L·T̄·ΔT/R      （L = 2.44e-8 W·Ω/K²）
            //   ⇒ **P·Q = L·T̄·ΔT·I²  与几何无关**（L/A 在两式里正好抵消）
            //   ⇒ 要「自己发的热盖住自己漏的热」即 P ≥ Q，代入得
            //     **P ≥ I·√(L·T̄·ΔT)**，对应引线压降 **ΔV ≥ √(L·T̄·ΔT)**。
            // 这是一条**不含任何几何量**的下界：舌片再怎么改形状都逃不掉，
            // 只能靠改 I、改 ΔT（接头温度）或认下这份发热。
            if (args.Contains("--busbar2"))
            {
                const double Lorenz = 2.44e-8;          // W·Ω/K²
                double wallB9 = 0.4, holeB9 = wallB9 + 25.0;
                double tRootB9 = 1150.0;
                double iEndB9 = 687.0, iShareB9 = 1105.0;

                var pB9 = SegmentSolver.Clone(p);
                pB9.Layer1.ThicknessMm = 10.0; pB9.Layer1.Enabled = true;
                pB9.WallMinMm = wallB9;
                pB9.FlangeInsulThickMm = 20; pB9.FlangeInsulated = true;
                pB9.BusbarClampLengthMm = 40;
                pB9.BusbarSinkTempC = 25;

                Console.WriteLine("=== 铜排：热导边界与维德曼–弗兰兹下界 ===");
                Console.WriteLine();
                Console.WriteLine("── ① 闭式下界：引线必然的热漏（**与几何无关**）");
                Console.WriteLine("   P·Q = L·T̄·ΔT·I²  ⇒  自给要求 P ≥ I·√(L·T̄·ΔT)，压降 ΔV ≥ √(L·T̄·ΔT)");
                Console.WriteLine($"{"接头°C",8}{"T̄ K",8}{"ΔT K",8}{"端片P_min W",14}{"共用P_min W",14}{"ΔV_min V",12}");
                foreach (double tj in new[] { 25.0, 100.0, 200.0, 400.0, 700.0, 1000.0 })
                {
                    double tBar = ((tRootB9 + 273.15) + (tj + 273.15)) / 2;
                    double dT = tRootB9 - tj;
                    double dv = Math.Sqrt(Lorenz * tBar * dT);
                    Console.WriteLine($"{tj,8:0}{tBar,8:0}{dT,8:0}{iEndB9 * dv,14:0}{iShareB9 * dv,14:0}{dv,12:0.000}");
                }
                Console.WriteLine();
                Console.WriteLine("★ 读法：接头越冷，引线必须发的热越多。接头 100 °C 时端片至少要发 ~110 W，");
                Console.WriteLine("  而 --endplate 实算端片舌片发热只有 46–185 W ⇒ **这条下界就在工作点上**，不是余量。");
                Console.WriteLine("  唯一能大幅松动它的是把接头做热（ΔT 小），代价是铜排本身耐不耐。");
                Console.WriteLine();

                Console.WriteLine("── ② 铜排热导 G 与它的电学代价（同一根铜排，两条性质由 WF 绑定）");
                Console.WriteLine($"{"G W/K",9}{"L/A m⁻¹",12}{"R_bus Ω",12}{"端片I²R W",12}" +
                                  $"{"压降 V",10}{"漏热@ΔT1050 W",16}  备注");
                foreach (double g in new[] { 0.005, 0.01, 0.02, 0.05, 0.1, 0.3, 1.1 })
                {
                    double loa = BusbarSizing.CuK / g;                 // L/A  [1/m]
                    double rBus = BusbarSizing.CuRho(100) * loa;
                    double pj = iEndB9 * iEndB9 * rBus;
                    Console.WriteLine($"{g,9:0.000}{loa,12:0}{rBus,12:E2}{pj,12:0}" +
                        $"{iEndB9 * rBus,10:0.000}{g * 1050,16:0}  " +
                        (Math.Abs(g - 1.1) < 1e-9 ? "§4.3a 选型的 40×21.8/300mm" :
                         pj > 300 ? "铜排自身发热过大" : "可做（细长铜排/热断）"));
                }
                Console.WriteLine();

                Console.WriteLine("── ③ 数值：把 --endplate ⑧ 的候选放到真实热导边界上");
                Console.WriteLine("   候选 盘Ø60／舌90末20×2.2／舌包 14 mm／圆盘包 20 mm");
                Console.WriteLine($"{"G W/K",9}{"接头°C",9}{"铜排带走W",11}{"舌发热",8}{"舌散热",8}" +
                                  $"{"抽管",8}{"残差",7}{"峰温",7}  判定");
                foreach (double g in new[] { -1.0, 0.005, 0.01, 0.02, 0.05, 0.1, 0.3, 1.1 })
                {
                    var gB9 = new FlangePlate
                    {
                        DiscRadiusMm = 30, HoleRadiusMm = holeB9,
                        TabEndXMm = -90, TabEndHalfWidthMm = 10,
                        ThicknessMm = 0.4, ThickenedMm = 0.4, TabThicknessMm = 2.2,
                        InsulBoundaryXMm = double.NaN, TabInsulThickMm = 14.0
                    };
                    var mB9 = FlangeMesher.Build(gB9, 0, 2.0, 11.0, 45.0, pB9.BusbarClampLengthMm);
                    var scB9 = ShellCurrent.Solve(mB9, iEndB9,
                                    Materials.PtResistivity(tRootB9) * 1e3, tRootB9);
                    var p2B9 = SegmentSolver.Clone(pB9);
                    p2B9.TSetC = tRootB9;
                    p2B9.BusbarClampTempC = -1;
                    p2B9.BusbarConductanceWPerK = g;
                    var thB9 = ShellThermal.Solve(mB9, scB9.JMagAPerMm2, p2B9, tRootB9,
                                    gB9.InsulBoundaryXResolved, tabBoundaryX: gB9.Tangent().X,
                                    tabInsulThickMm: gB9.TabInsulThickMm);
                    string vB9 = thB9.OverMelt ? "✗✗ 已熔"
                               : Math.Abs(thB9.QFromTubeW) < 15 ? "✓ 近平衡"
                               : thB9.QFromTubeW >= 15 ? "✗ 抽管" : "✗ 倒灌";
                    if (!thB9.OverMelt && thB9.TTabEndMeanC > 1085) vB9 += "／接头>铜熔点";
                    else if (!thB9.OverMelt && thB9.TTabEndMeanC > 400) vB9 += "／接头过热";
                    Console.WriteLine($"{(g < 0 ? "自由端" : g.ToString("0.000")),9}{thB9.TTabEndMeanC,9:0}" +
                        $"{thB9.QToClampW,11:0}{thB9.QGenTabW,8:0}{thB9.QLossTabW,8:0}" +
                        $"{(thB9.QFromTubeW >= 0 ? "+" : "−") + Math.Abs(thB9.QFromTubeW).ToString("0"),8}" +
                        $"{(thB9.EnergyResidualW >= 0 ? "+" : "−") + Math.Abs(thB9.EnergyResidualW).ToString("0.0"),7}" +
                        $"{thB9.TMaxC,7:0}  {vB9}");
                }
                Console.WriteLine();
                Console.WriteLine("★ 这一节要回答的是：**存不存在一个 G，让接头温度铜受得了、同时抽管≈0**。");
                Console.WriteLine("  若两者的 G 区间不相交，则舌片必须加长/加厚以提供更多串联热阻 —— 那是要花铂的。");
                return;
            }

            // --cli --balance   ★ 能量对账：法兰的四项收支，用**独立算出**的量核对
            //
            // 之前铜排那一项是用恒等式反推的，所以「平衡」是循环论证。现在四项全独立：
            //   发热 = Σ ρe·J²·t·A（体积分）　　散热 = Σ 2q″(T)·A（面积分）
            //   管孔 = Σ 导度×温差（孔单元）　　铜排 = Σ 导度×温差（舌端单元）
            // 闭合式：自由单元的(发热−散热) + 管孔净流入 − 铜排带走 = 0
            if (args.Contains("--balance"))
            {
                double wallB2 = 0.4, insB2 = 10.0, holeB2 = wallB2 + 25.0;
                double discB2 = 36.0, tabLB2 = 90.0, hwB2 = 30.0, ttB2 = 0.8;

                var pB2 = SegmentSolver.Clone(p);
                pB2.Layer1.ThicknessMm = insB2; pB2.Layer1.Enabled = true;
                pB2.WallMinMm = wallB2;
                pB2.FlangeInsulThickMm = 20; pB2.FlangeInsulated = true;
                pB2.BusbarClampTempC = 300; pB2.BusbarClampLengthMm = 40;

                FlangePlate MkB2(double td) => new()
                {
                    DiscRadiusMm = discB2, HoleRadiusMm = holeB2,
                    TabEndXMm = -tabLB2, TabEndHalfWidthMm = hwB2,
                    ThicknessMm = td, ThickenedMm = td, TabThicknessMm = ttB2,
                    InsulBoundaryXMm = double.NaN
                };

                Console.WriteLine("=== 法兰能量对账 ===");
                Console.WriteLine($"几何 盘Ø{2 * discB2:0}／舌 {tabLB2:0}×{2 * hwB2:0}×{ttB2:0.0}／" +
                                  $"压接 40／夹持 300 °C／圆盘包 20、舌片裸露");
                Console.WriteLine("闭合式：自由单元(发热−散热) + 管孔净流入 − 铜排带走 = 0");
                Console.WriteLine();

                var lcB2 = new LineCase
                {
                    Base = pB2, WallMm = wallB2, UseMeasuredCurrent = false, CheckRamp = false,
                    SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                    FlangePlates = new[] { MkB2(0.4), MkB2(0.4), MkB2(0.4), MkB2(0.4) }
                };
                var rB2 = LineRunner.Run(lcB2);
                if (!rB2.Ok) { Console.WriteLine("✗ " + rB2.Message); return; }

                Console.WriteLine($"{"片",10}{"电流A",8}{"发热W",9}{"表面散热",10}" +
                                  $"{"管孔流入",10}{"铜排带走",10}{"残差W",9}{"残差/发热",10}");
                foreach (var f11 in rB2.Flanges)
                    Console.WriteLine($"{f11.Name,10}{f11.CurrentA,8:0}{f11.QGenW,9:0}{f11.QLossW,10:0}" +
                        $"{f11.QFromTubeW,10:+0;-0}{f11.QClampW,10:0}{f11.EnergyResidualW,9:+0.0;-0.0}" +
                        $"{f11.EnergyResidualW / Math.Max(1e-9, f11.QGenW) * 100,9:0.0}%");

                Console.WriteLine();
                Console.WriteLine("读法：");
                Console.WriteLine("· **残差 ≈ 0** ⇒ 场解的能量账是自洽的，四项可信；");
                Console.WriteLine("  残差显著 ⇒ 场解没收敛或边界处理有漏，任何结论都不能用。");
                Console.WriteLine("· 「管孔流入」为正 = 法兰在从管子抽热 ⇒ 管根变冷 ⇒ 管根温差为正。");
                Console.WriteLine("· 若「铜排带走」远大于「发热−散热」，缺口就只能由管子补 —— 那正是管根塌的原因。");
                Console.WriteLine();
                Console.WriteLine($"管根温差 " + string.Join(" / ",
                    rB2.Segments.Select(x => x.RootDeltaK.ToString("+0.0;-0.0"))) + " K" +
                    (rB2.Converged ? "　耦合收敛 ✓" : "　⚠ 未收敛"));
                return;
            }

            // --cli --jmap   ★ 电流密度全景：三处（管/圆盘/舌片）× 两工况（升温空管/稳态有玻璃）
            //
            // 用户 2026-08-14：「升温 20°C/HR（空管）与稳态（有玻璃液），
            // 舌、圆盘法兰与管的 J 是不一样的」——对，而且**限值也不一样**：
            //   管   ：控温维持温度，限值是热稳定极限
            //   圆盘 ：孔周峰值，但紧贴管子 ⇒ 横向导热撑着，限值宽松
            //   舌片 ：离冷源远 ⇒ 表面散热是主要恢复力，限值最严
            // 两工况的差别：升温空管无玻璃负荷也无玻璃帮忙；
            //   稳态下 HC2/HC3 的玻璃比管热，反而在加热管子 ⇒ 电流更小。
            if (args.Contains("--jmap"))
            {
                double wallJ = 0.4, insJ = 10.0, holeJ = wallJ + 25.0;
                double discJ = 30.0, tabLJ = 90.0, hwJ = 30.0, ttJ = 1.5;
                double tDiscJ = 0.855;
                double fJ = Math.Sqrt(3.0);

                var pJ = SegmentSolver.Clone(p);
                pJ.Layer1.ThicknessMm = insJ; pJ.Layer1.Enabled = true;
                pJ.WallMinMm = wallJ;
                pJ.FlangeInsulThickMm = 20; pJ.FlangeInsulated = true;
                pJ.BusbarClampTempC = 300; pJ.BusbarClampLengthMm = 40;

                var gJ = new FlangePlate
                {
                    DiscRadiusMm = discJ, HoleRadiusMm = holeJ,
                    TabEndXMm = -tabLJ, TabEndHalfWidthMm = hwJ,
                    ThicknessMm = tDiscJ, ThickenedMm = tDiscJ,
                    TabThicknessMm = ttJ, InsulBoundaryXMm = double.NaN
                };
                var mJ = FlangeMesher.Build(gJ, 0, 2.0, 11.0, 45.0, 40.0);
                var sfJ = DesignScreen.Extract(mJ, 1000.0, 1050.0, gJ.Tangent().X);
                double areaTubeJ = Math.PI * wallJ * (pJ.TubeIdMm + wallJ);

                Console.WriteLine("=== 电流密度全景：三处 × 两工况 ===");
                Console.WriteLine($"几何：盘 Ø{2 * discJ:0}／盘厚 {tDiscJ:0.000}／舌 {tabLJ:0}×{2 * hwJ:0}×{ttJ:0.0}" +
                                  $"／管壁 {wallJ:0.0}（截面 {areaTubeJ:0.0} mm²）");
                Console.WriteLine($"保温：管纤维 {insJ:0}／圆盘包 20／**舌片裸露**");
                Console.WriteLine();

                Console.WriteLine("── 工况一：升温（空管，20 °C/h，温控功率）");
                Console.WriteLine($"{"管温°C",8}{"段电流A",9}{"管J",7}" +
                                  $"{"盘J_max",9}{"盘J_stab",10}{"盘裕度",8}" +
                                  $"{"舌J",7}{"舌J_stab",10}{"舌裕度",8}  判定");
                foreach (double tC in new[] { 100.0, 300.0, 600.0, 900.0, 1150.0 })
                {
                    double iSeg = RampTwoNode.QuasiStaticCurrentA(pJ, wallJ, tC, 20.0);
                    double iPlate = fJ * iSeg;                       // 共用片
                    double jTube = iSeg / areaTubeJ;
                    double jDisc = sfJ.ShapeJ * iPlate / tDiscJ;
                    double jTab = iPlate / (2 * hwJ * ttJ);
                    // 圆盘热点：紧贴管孔，横向长度取环宽
                    var sd = LocalStability.Check(pJ, tC, jDisc, tDiscJ, 20.0, discJ - holeJ);
                    // 舌片中段：裸露，横向长度取到冷源的一半
                    var st2 = LocalStability.Check(pJ, tC, jTab, ttJ, 0.0, (tabLJ - 40) / 2);
                    Console.WriteLine($"{tC,8:0}{iSeg,9:0}{jTube,7:0.00}" +
                        $"{jDisc,9:0.00}{sd.JStab,10:0.00}{sd.Margin,8:0.00}" +
                        $"{jTab,7:0.00}{st2.JStab,10:0.00}{st2.Margin,8:0.00}  " +
                        (sd.Stable && st2.Stable ? "✓" :
                         (!sd.Stable ? "✗盘" : "") + (!st2.Stable ? "✗舌" : "")));
                }

                Console.WriteLine();
                Console.WriteLine("── 工况二：稳态（有玻璃）—— 由整线耦合解给出真实段电流");
                var lcJ = new LineCase
                {
                    Base = pJ, WallMm = wallJ, UseMeasuredCurrent = false, CheckRamp = false,
                    SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                    FlangePlates = new[] { gJ, gJ, gJ, gJ }
                };
                try
                {
                    var rJ = LineRunner.Run(lcJ);
                    if (rJ.Ok)
                    {
                        Console.WriteLine($"{"段/片",10}{"电流A",9}{"管J",7}" +
                                          $"{"盘J_max",9}{"盘J_stab",10}{"盘裕度",8}" +
                                          $"{"舌J",7}{"舌J_stab",10}{"舌裕度",8}  判定");
                        for (int i2 = 0; i2 < rJ.Segments.Length; i2++)
                        {
                            var sg = rJ.Segments[i2];
                            Console.WriteLine($"{sg.Name + " 管",10}{sg.CurrentA,9:0}{sg.TubeJAPerMm2,7:0.00}" +
                                              $"{"—",9}{"—",10}{"—",8}{"—",7}{"—",10}{"—",8}");
                        }
                        foreach (var f10 in rJ.Flanges)
                        {
                            double jTab2b = f10.CurrentA / (2 * hwJ * ttJ);
                            var sdb = LocalStability.Check(pJ, f10.TRootC, f10.JMaxAPerMm2,
                                          tDiscJ, 20.0, discJ - holeJ);
                            var stb = LocalStability.Check(pJ, 800, jTab2b, ttJ, 0.0, (tabLJ - 40) / 2);
                            Console.WriteLine($"{f10.Name,10}{f10.CurrentA,9:0}{"—",7}" +
                                $"{f10.JMaxAPerMm2,9:0.00}{sdb.JStab,10:0.00}{sdb.Margin,8:0.00}" +
                                $"{jTab2b,7:0.00}{stb.JStab,10:0.00}{stb.Margin,8:0.00}  " +
                                (sdb.Stable && stb.Stable ? "✓" :
                                 (!sdb.Stable ? "✗盘" : "") + (!stb.Stable ? "✗舌" : "")));
                        }
                        Console.WriteLine($"  管根温差 " + string.Join(" / ",
                            rJ.Segments.Select(x => x.RootDeltaK.ToString("+0.0;-0.0"))) + " K" +
                            (rJ.Converged ? "　收敛 ✓" : "　⚠ 未收敛"));
                    }
                    else Console.WriteLine("  稳态求解失败：" + rJ.Message);
                }
                catch (Exception ex) { Console.WriteLine("  稳态异常：" + ex.Message); }

                Console.WriteLine();
                Console.WriteLine("★ 两工况必须都过。升温期的特点：温度低 ⇒ 表面散热按 T⁴ 塌下去，");
                Console.WriteLine("  而 TCR 升高（冷态是热态的 6.5 倍）⇒ **舌片这种靠表面散热的部位在冷态最脆弱**。");
                Console.WriteLine("  圆盘则不同：它靠横向导热到管子，那一项与温度关系不大。");
                return;
            }

            // --cli --design   ★★★ 最终搜索：完整约束集下求最轻可行方案
            //
            // 取代此前一串临时扫描。约束全部来自计算或现场实务，不再有「我随手写的值」：
            //
            //   ① 管根温差 0 < ΔT < 10 K                  业主给的核心约束
            //   ② 法兰局部最高温 ≤ 管根温度                业主原始规则，不放宽
            //   ③ 压接长 ≥ 40 mm                          **现场实务下界（C 型夹也做不到更短）**
            //   ④ 压接界面电流密度 ≤ 1 A/mm²（双面夹）      压接接头常规
            //   ⑤ 有效导热长（舌长 − 压接长）≥ 45 mm       实测：低于此管根温差急剧恶化
            //   ⑥ 圆盘环宽 ≥ 10 mm                        可焊、可搬运
            //   ⑦ 所有厚度 ≥ 0.4 mm                       业主给的工艺下界
            //
            // ③ 与 ⑤ 合起来 ⇒ **舌长 ≥ 85 mm**。此前那版 50 mm 舌片方案由此判死。
            // 同时对比单舌片与**双舌片对称进电**。
            if (args.Contains("--design"))
            {
                const double clampMin = 40.0, effMin = 45.0, ringMin = 10.0, tMin2 = 0.4;
                double wallD = 0.4, insD = 10.0, holeD = wallD + 25.0;

                Console.WriteLine("=== 最终搜索：完整约束集下求最轻可行方案 ===");
                Console.WriteLine($"压接 ≥{clampMin:0} mm（现场实务）｜有效导热长 ≥{effMin:0} mm｜" +
                                  $"环宽 ≥{ringMin:0} mm｜厚度 ≥{tMin2:0.0} mm");
                Console.WriteLine($"⇒ 舌长 ≥ {clampMin + effMin:0} mm");
                Console.WriteLine("管壁 0.4／管纤维 10／**仅圆盘保温 20、舌片裸露**／夹持 300 °C；盘厚每格自动定厚");
                Console.WriteLine("★ 舌片裸露是算出来的：包保温会把舌片允许 J 从 24.6 砍到 15.0 A/mm²");
                Console.WriteLine();
                Console.WriteLine($"{"舌数",5}{"盘Ø",6}{"环宽",6}{"舌长",6}{"舌半宽",7}{"舌厚",6}" +
                                  $"{"每舌A",7}{"J_舌",7}{"界面J",7}" +
                                  $"{"盘厚 mm",20}{"ΔT范围",13}{"盘局部",8}{"舌J_stab",9}{"总铂g",8}  判定");

                var best = (m: double.MaxValue, d: "");
                foreach (int nTab in new[] { 1, 2 })
                    foreach (double disc in new[] { 36.0, 44.0 })
                    {
                        double ring = disc - holeD;
                        if (ring < ringMin) continue;
                        foreach (double tabL4 in new[] { 90.0, 130.0 })
                            foreach (double hw3 in new[] { 30.0 })
                                foreach (double tt3 in new[] { 0.8, 1.5, 2.5 })
                                {
                                    if (tabL4 - clampMin < effMin) continue;
                                    if (disc >= Math.Sqrt(tabL4 * tabL4 + hw3 * hw3)) continue;
                                    double iTab = 1099.0 / nTab;
                                    double jTab2 = iTab / (2 * hw3 * tt3);
                                    double jFace4 = iTab / (2 * clampMin * 2 * hw3);
                                    if (jFace4 > 1.0) continue;

                                    var pD = SegmentSolver.Clone(p);
                                    pD.Layer1.ThicknessMm = insD; pD.Layer1.Enabled = true;
                                    pD.WallMinMm = wallD;
                                    pD.FlangeInsulThickMm = 20; pD.FlangeInsulated = true;
                                    pD.BusbarClampTempC = 300;
                                    pD.BusbarClampLengthMm = clampMin;

                                    FlangePlate MkD(double td) => new()
                                    {
                                        DiscRadiusMm = disc, HoleRadiusMm = holeD,
                                        TabEndXMm = -tabL4, TabEndHalfWidthMm = hw3,
                                        ThicknessMm = td, ThickenedMm = td,
                                        TabThicknessMm = tt3,
                                        // ★ 仅圆盘保温、舌片裸露（NaN = 自动取切点）。
                                        //   早先「全包 20mm」对圆盘无害、对舌片有害：
                                        //   舌片离冷源远，表面散热是它抗局部热失稳的主要恢复力。
                                        InsulBoundaryXMm = double.NaN,
                                        TwoTabs = nTab == 2
                                    };
                                    var lcD = new LineCase
                                    {
                                        Base = pD, WallMm = wallD, UseMeasuredCurrent = false,
                                        CheckRamp = false, SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                                        FlangePlates = new[] { MkD(0.8), MkD(1.2), MkD(1.1), MkD(0.7) }
                                    };
                                    var rD = FlangeAutoSizer.SolveAuto(lcD, MkD,
                                                 new[] { 0.8, 1.2, 1.1, 0.7 },
                                                 new FlangeAutoSizer.Options { MinThickMm = tMin2 },
                                                 new SyncProgress<string>(_ => { }), default);
                                    if (rD.Line is not { Ok: true } lr5)
                                    {
                                        // ★ 一定要打印原因。早先写成静默 continue，
                                        //   结果整张表零行输出，查不出是被过滤还是求解挂了。
                                        Console.WriteLine($"{nTab,5}{2 * disc,6:0}{ring,6:0.0}{tabL4,6:0}" +
                                            $"{hw3,7:0}{tt3,6:0.0}   ✗ 求解失败：" +
                                            (rD.Line is null ? rD.Message : rD.Line.Message));
                                        continue;
                                    }

                                    double dmin = lr5.Segments.Min(x => x.RootDeltaK);
                                    double dmax = lr5.Segments.Max(x => x.RootDeltaK);
                                    double exc = lr5.Flanges.Max(f9 => f9.TMaxC - f9.TRootC);

                                    // ★ 舌片局部热失稳：中段离两端冷源最远，是最脆弱处。
                                    //   恢复力 = 表面散热(裸露) + 横向导热到冷源
                                    double tTabC = 800;                       // 舌片中段典型温度
                                    var stTab = LocalStability.Check(pD, tTabC, jTab2, tt3,
                                                    insulThickMm: 0,          // 舌片裸露
                                                    lateralLenMm: (tabL4 - clampMin) / 2);
                                    bool tabOk = stTab.Stable;

                                    bool ok = rD.Converged && lr5.Converged
                                              && dmin > 0 && dmax <= 10 && exc <= 0 && tabOk;
                                    Console.WriteLine($"{nTab,5}{2 * disc,6:0}{ring,6:0.0}{tabL4,6:0}" +
                                        $"{hw3,7:0}{tt3,6:0.0}{iTab,7:0}{jTab2,7:0.0}{jFace4,7:0.00}" +
                                        $"{string.Join("/", rD.ThicknessMm.Select(x => x.ToString("0.00"))),20}" +
                                        $"{$"{dmin:+0.0;-0.0}~{dmax:+0.0;-0.0}",13}{exc,10:+0.0;-0.0}" +
                                        $"{stTab.JStab,7:0.0}{lr5.TotalMassG,8:0}  " +
                                        (ok ? "✓" : (dmin <= 0 || dmax > 10 ? "✗ΔT" : "") +
                                                    (exc > 0 ? "✗盘局部" : "") +
                                                    (!tabOk ? $"✗舌失稳(裕{stTab.Margin:0.00})" : "")));
                                    if (ok && lr5.TotalMassG < best.m)
                                        best = (lr5.TotalMassG,
                                            $"{nTab} 舌／Ø{2 * disc:0}（环宽 {ring:0.0}）／舌 {tabL4:0}×{2 * hw3:0}×{tt3:0.0}／" +
                                            $"压接 {clampMin:0}／盘厚 " +
                                            string.Join("/", rD.ThicknessMm.Select(x => x.ToString("0.00"))));
                                }
                    }
                Console.WriteLine();
                if (best.m < double.MaxValue)
                {
                    Console.WriteLine($"★ 完整约束下最轻：{best.m:0} g　{best.d}");
                    Console.WriteLine($"  相对现状 7141 g 省 {(7141 - best.m) / 7141 * 100:0.0} %");
                }
                else
                    Console.WriteLine("✗ 本轮无全过方案 —— 需放开盘径/舌长/厚度范围，或松某条约束。");
                return;
            }

            // --cli --feasible   ★★ 先可行、再最轻（用户 2026-08-13：「该加重就加重，
            //                        优化到烧断或不能施工就没意义」）
            //
            // 此前一路把参数往工艺下界压，压出「环宽 4.6 mm、厚 0.5 mm 的铂环焊在 0.4 mm 管上」
            // 这种算得过但未必做得出的东西。现在把之前放宽/绕开的几条收回来当**硬约束**：
            //
            //   ① 管根温差 0 < ΔT < 10 K            （业主给的核心约束）
            //   ② 法兰局部最高温 ≤ 管根温度          （业主原始规则，不再放宽）
            //   ③ 压接界面电流密度 ≤ 1 A/mm²（双面） （压接接头常规）
            //   ④ 压接长度 ≥ 20 mm                   （可夹性 —— 3 mm 夹不住）
            //   ⑤ 圆盘环宽 ≥ 可加工下限              （可焊、可搬运）
            //   ⑥ 所有厚度 ≥ 0.4 mm                  （业主给的工艺下界）
            //
            // 目标：在**全部满足**的解里取总铂最小。宁可重，不要做不出来。
            if (args.Contains("--feasible"))
            {
                int mi = Array.IndexOf(args, "--minring");
                double minRing = mi >= 0 && mi + 1 < args.Length && double.TryParse(args[mi + 1], out var mr)
                                 ? mr : 10.0;      // 圆盘环宽下限 mm，可用 --minring 改

                double wallF3 = 0.4, insF3 = 10.0, holeF3 = wallF3 + 25.0;
                double iShared3 = 1099;

                Console.WriteLine("=== 先可行、再最轻 ===");
                Console.WriteLine($"硬约束：0<ΔT<10 K｜局部最高温 ≤ 管根｜界面J ≤1｜压接 ≥20 mm｜" +
                                  $"环宽 ≥{minRing:0} mm｜厚度 ≥0.4 mm");
                Console.WriteLine("目标：全部满足者中取总铂最小。**宁可重，不要做不出来。**");
                Console.WriteLine();
                Console.WriteLine($"{"盘Ø",6}{"环宽",6}{"舌半宽",7}{"舌厚",6}{"舌长",6}" +
                                  $"{"J_舌片",8}{"压接",6}{"界面J",7}" +
                                  $"{"盘厚 mm",20}{"ΔT范围",14}{"局部−管根",11}{"总铂g",8}  判定");

                var best = (m: double.MaxValue, d: "");
                foreach (double disc in new[] { 36.0, 44.0, 55.0 })
                {
                    double ring = disc - holeF3;
                    if (ring < minRing) continue;
                    foreach (double hw2 in new[] { 25.0, 35.0 })
                        foreach (double tt2 in new[] { 0.8, 1.5 })
                            foreach (double tabL3 in new[] { 80.0, 120.0 })
                            {
                                double clampL3 = Math.Max(20.0, Math.Ceiling(iShared3 / (4 * hw2)));
                                double jFace3 = iShared3 / (2 * clampL3 * 2 * hw2);
                                if (jFace3 > 1.0) continue;
                                if (tabL3 - clampL3 < 30) continue;
                                if (disc >= Math.Sqrt(tabL3 * tabL3 + hw2 * hw2)) continue;

                                var pF3 = SegmentSolver.Clone(p);
                                pF3.Layer1.ThicknessMm = insF3; pF3.Layer1.Enabled = true;
                                pF3.WallMinMm = wallF3;
                                pF3.FlangeInsulThickMm = 20; pF3.FlangeInsulated = true;
                                pF3.BusbarClampTempC = 300;
                                pF3.BusbarClampLengthMm = clampL3;

                                FlangePlate MkF3(double td) => new()
                                {
                                    DiscRadiusMm = disc, HoleRadiusMm = holeF3,
                                    TabEndXMm = -tabL3, TabEndHalfWidthMm = hw2,
                                    ThicknessMm = td, ThickenedMm = td,
                                    TabThicknessMm = tt2, InsulBoundaryXMm = -1e9
                                };
                                var lcF3 = new LineCase
                                {
                                    Base = pF3, WallMm = wallF3, UseMeasuredCurrent = false,
                                    CheckRamp = false, SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                                    FlangePlates = new[] { MkF3(0.8), MkF3(1.2), MkF3(1.1), MkF3(0.7) }
                                };
                                var rF3 = FlangeAutoSizer.SolveAuto(lcF3, MkF3,
                                              new[] { 0.8, 1.2, 1.1, 0.7 }, new FlangeAutoSizer.Options(),
                                              new SyncProgress<string>(_ => { }), default);
                                if (rF3.Line is not { Ok: true } lr4) continue;

                                double dmin = lr4.Segments.Min(x => x.RootDeltaK);
                                double dmax = lr4.Segments.Max(x => x.RootDeltaK);
                                double localExcess = lr4.Flanges.Max(f7 => f7.TMaxC - f7.TRootC);
                                bool ok = rF3.Converged && lr4.Converged
                                          && dmin > 0 && dmax <= 10
                                          && localExcess <= 0;                    // ② 不再放宽
                                Console.WriteLine($"{2 * disc,6:0}{ring,6:0.0}{hw2,7:0}{tt2,6:0.0}{tabL3,6:0}" +
                                    $"{iShared3 / (2 * hw2 * tt2),8:0.0}{clampL3,6:0}{jFace3,7:0.00}" +
                                    $"{string.Join("/", rF3.ThicknessMm.Select(x => x.ToString("0.00"))),20}" +
                                    $"{$"{dmin:+0.0;-0.0}~{dmax:+0.0;-0.0}",14}{localExcess,11:+0.0;-0.0}" +
                                    $"{lr4.TotalMassG,8:0}  " +
                                    (ok ? "✓" : (dmin <= 0 || dmax > 10 ? "✗ΔT" : "") +
                                                (localExcess > 0 ? "✗局部" : "")));
                                if (ok && lr4.TotalMassG < best.m)
                                    best = (lr4.TotalMassG,
                                            $"Ø{2 * disc:0}／环宽{ring:0.0}／舌 {tabL3:0}×{2 * hw2:0}×{tt2:0.0}／" +
                                            $"压接{clampL3:0}／盘厚 " +
                                            string.Join("/", rF3.ThicknessMm.Select(x => x.ToString("0.00"))));
                            }
                }
                Console.WriteLine();
                if (best.m < double.MaxValue)
                {
                    Console.WriteLine($"★ 全部约束都过的最轻方案：{best.m:0} g　{best.d}");
                    Console.WriteLine($"  相对现状 7141 g 省 {(7141 - best.m) / 7141 * 100:0.0} %");
                }
                else
                    Console.WriteLine("✗ 本轮无全过方案 —— 需放开搜索范围（更大的盘/更长的舌/更厚），或松某条约束。");
                return;
            }

            // --cli --tabsearch   ★ 舌片截面搜索：宽 × 厚 × 长，压接长由电气自动定
            //
            // 用户 2026-08-13：「改截面（宽度与厚度），一切以计算结果说话，
            // 铜排也需要合理的长度接触舌部（3mm 怎么夹？）」
            //
            // 三条互相拉扯：
            //   舌片截面 ↑ ⇒ J_舌片 ↓（利电气）、导热到铜排 ↑（不利管根温差）、铂重 ↑
            //   压接长 ↑   ⇒ 界面 J ↓（利电气）、定温边界推近圆盘（不利管根温差）
            //   舌长 ↑     ⇒ 有效导热长 ↑（利管根温差）、铂重 ↑
            // 圆盘厚度每格重新自动定厚。
            if (args.Contains("--tabsearch"))
            {
                double wallS2 = 0.4, insS2 = 10.0, holeS2 = wallS2 + 25.0, discS2 = 30.0;
                double iShared2 = 1099;

                Console.WriteLine("=== 舌片截面搜索（宽 × 厚 × 长）===");
                Console.WriteLine($"圆盘 Ø{2 * discS2:0} 固定；圆盘厚度每格自动定厚；共用片 {iShared2:0} A");
                Console.WriteLine("压接长按「界面电流密度 ≤1 A/mm²（双面夹）」自动取，且不小于 15 mm（可夹性）");
                Console.WriteLine();
                Console.WriteLine($"{"舌半宽",7}{"舌厚",6}{"舌长",6}{"截面mm²",9}{"J_舌片",8}" +
                                  $"{"压接长",7}{"界面J",7}{"有效长",7}" +
                                  $"{"盘厚 mm",22}{"最差ΔT",9}{"铜排W",7}{"总铂g",8}  判定");

                foreach (double hw in new[] { 20.0, 30.0, 40.0 })
                    foreach (double tt in new[] { 0.5, 1.0, 2.0 })
                        foreach (double tabL2 in new[] { 60.0, 90.0 })
                        {
                            double sect = 2 * hw * tt;
                            double jTab = iShared2 / sect;
                            // 压接长：界面 J ≤1（双面夹，接触宽 = 2*hw）且 ≥15 mm 才夹得住
                            double clampL2 = Math.Max(15.0, Math.Ceiling(iShared2 / (2 * 2 * hw)));
                            double jFace2 = iShared2 / (2 * clampL2 * 2 * hw);
                            double effL = tabL2 - clampL2;
                            if (effL < 20) continue;                    // 有效导热长太短，先排除

                            var pS3 = SegmentSolver.Clone(p);
                            pS3.Layer1.ThicknessMm = insS2; pS3.Layer1.Enabled = true;
                            pS3.WallMinMm = wallS2;
                            pS3.FlangeInsulThickMm = 20; pS3.FlangeInsulated = true;
                            pS3.BusbarClampTempC = 300;
                            pS3.BusbarClampLengthMm = clampL2;

                            // ★ 舌片厚度独立于圆盘：TabThicknessMm 非 NaN 即生效
                            FlangePlate MkS3(double tDisc) => new()
                            {
                                DiscRadiusMm = discS2, HoleRadiusMm = holeS2,
                                TabEndXMm = -tabL2, TabEndHalfWidthMm = hw,
                                ThicknessMm = tDisc, ThickenedMm = tDisc,
                                TabThicknessMm = tt,
                                InsulBoundaryXMm = -1e9
                            };
                            var lcS3 = new LineCase
                            {
                                Base = pS3, WallMm = wallS2, UseMeasuredCurrent = false, CheckRamp = false,
                                SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                                FlangePlates = new[] { MkS3(0.5), MkS3(0.9), MkS3(0.8), MkS3(0.45) }
                            };
                            var rS3 = FlangeAutoSizer.SolveAuto(lcS3, MkS3, new[] { 0.5, 0.9, 0.8, 0.45 },
                                          new FlangeAutoSizer.Options(),
                                          new SyncProgress<string>(_ => { }), default);
                            if (rS3.Line is not { Ok: true } lr3)
                            { Console.WriteLine($"{hw,7:0}{tt,6:0.0}{tabL2,6:0}   求解失败"); continue; }
                            double dmin = lr3.Segments.Min(x => x.RootDeltaK);
                            double dmax = lr3.Segments.Max(x => x.RootDeltaK);
                            double worst = Math.Abs(dmax - 5) > Math.Abs(dmin - 5) ? dmax : dmin;
                            bool ok = rS3.Converged && lr3.Converged && dmin > 0 && dmax <= 10;
                            Console.WriteLine($"{hw,7:0}{tt,6:0.0}{tabL2,6:0}{sect,9:0}{jTab,8:0.0}" +
                                $"{clampL2,7:0}{jFace2,7:0.00}{effL,7:0}" +
                                $"{string.Join("/", rS3.ThicknessMm.Select(x => x.ToString("0.00"))),22}" +
                                $"{worst,9:+0.0;-0.0}{lr3.Flanges.Sum(f6 => f6.QClampW),7:0}" +
                                $"{lr3.TotalMassG,8:0}  " + (ok ? "✓" : "✗"));
                        }
                Console.WriteLine();
                Console.WriteLine("读法：先看「判定 ✓」，再在其中挑总铂最小。");
                Console.WriteLine("J_舌片 是舌片自身截面的电流密度（此前一直没单独报，它比孔周更窄）。");
                Console.WriteLine("界面 J 是压接面的，按双面夹算；≤1 A/mm² 是压接接头的常规量级。");
                return;
            }

            // --cli --tabclamp   ★ 舌片长度 × 压接长度 二维扫描
            //
            // --clamplen 证实两侧冲突：电气要压接 ≥20 mm，热学只允许 3 mm，中间无交集。
            // 出路是把压接区**往外挪**（加长舌片）而不是往里扩，使**有效导热长度**不缩短。
            // 每个格子都重新自动定厚 —— 否则几何变了厚度没跟着变，比较不公平。
            if (args.Contains("--tabclamp"))
            {
                double wallT2 = 0.4, insT2 = 10.0, holeT2 = wallT2 + 25.0, discT2 = 30.0, tabWT2 = 20.0;
                double iShared = 1099;                 // 共用片电流（用于界面电流密度）

                Console.WriteLine("=== 舌片长度 × 压接长度 二维扫描 ===");
                Console.WriteLine($"圆盘 Ø{2 * discT2:0}　舌宽 {2 * tabWT2:0} mm　共用片 {iShared:0} A");
                Console.WriteLine("每格重新自动定厚；界面电流密度按**双面夹**算，目标 ≤1 A/mm²");
                Console.WriteLine("有效导热长 = 舌长 − 压接长（定温边界到圆盘的距离）");
                Console.WriteLine();
                Console.WriteLine($"{"舌长",6}{"压接",6}{"有效长",8}{"界面J",8}" +
                                  $"{"四片厚度 mm",26}{"最差ΔT",9}{"铜排 W",8}{"总铂 g",8}  判定");

                foreach (double tabL in new[] { 50.0, 70.0, 90.0, 120.0 })
                    foreach (double clampL in new[] { 3.0, 20.0, 30.0 })
                    {
                        if (clampL >= tabL - 10) continue;
                        double jFace = iShared / (2 * clampL * 2 * tabWT2);
                        bool elecOk = jFace <= 1.0;

                        var pT = SegmentSolver.Clone(p);
                        pT.Layer1.ThicknessMm = insT2; pT.Layer1.Enabled = true;
                        pT.WallMinMm = wallT2;
                        pT.FlangeInsulThickMm = 20; pT.FlangeInsulated = true;
                        pT.BusbarClampTempC = 300;
                        pT.BusbarClampLengthMm = clampL;

                        FlangePlate MkT2(double t) => new()
                        {
                            DiscRadiusMm = discT2, HoleRadiusMm = holeT2,
                            TabEndXMm = -tabL, TabEndHalfWidthMm = tabWT2,
                            ThicknessMm = t, ThickenedMm = t, InsulBoundaryXMm = -1e9
                        };
                        var lcT2 = new LineCase
                        {
                            Base = pT, WallMm = wallT2, UseMeasuredCurrent = false, CheckRamp = false,
                            SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                            FlangePlates = new[] { MkT2(0.5), MkT2(0.9), MkT2(0.8), MkT2(0.45) }
                        };
                        var rT2 = FlangeAutoSizer.SolveAuto(lcT2, MkT2, new[] { 0.5, 0.9, 0.8, 0.45 },
                                      new FlangeAutoSizer.Options(),
                                      new SyncProgress<string>(_ => { }), default);
                        if (rT2.Line is not { Ok: true } lr2)
                        { Console.WriteLine($"{tabL,6:0}{clampL,6:0}   求解失败"); continue; }
                        double dmin = lr2.Segments.Min(x => x.RootDeltaK);
                        double dmax = lr2.Segments.Max(x => x.RootDeltaK);
                        double worst = Math.Abs(dmax - 5) > Math.Abs(dmin - 5) ? dmax : dmin;
                        bool thermOk = rT2.Converged && lr2.Converged && dmin > 0 && dmax <= 10;
                        Console.WriteLine($"{tabL,6:0}{clampL,6:0}{tabL - clampL,8:0}{jFace,8:0.00}" +
                            $"{string.Join("/", rT2.ThicknessMm.Select(x => x.ToString("0.000"))),26}" +
                            $"{worst,9:+0.0;-0.0}{lr2.Flanges.Sum(f5 => f5.QClampW),8:0}" +
                            $"{lr2.TotalMassG,8:0}  " +
                            (elecOk && thermOk ? "✓ 两侧都过"
                             : !elecOk && !thermOk ? "✗ 两侧都不过"
                             : !elecOk ? "✗ 电气" : "✗ 热学"));
                    }
                Console.WriteLine();
                Console.WriteLine("★ 找「✓ 两侧都过」里总铂最小的那格。");
                Console.WriteLine("  预期规律：只要**有效导热长**不低于约 47 mm，热学就守得住；");
                Console.WriteLine("  压接长则由电气单独决定。两者靠加长舌片解耦，代价是铂重。");
                return;
            }

            // --cli --clamplen   ★ 铜排压接长度：接触电流密度 vs 热学代价
            //
            // 用户 2026-08-13：「舌片最末端 3-4 mm 这不现实，接触铜排 J 超大」——对。
            // 3 mm × 40 mm = 120 mm²，共用片 1099 A ⇒ 界面 9.2 A/mm²，
            // 而压接接头通常按 ≤1 A/mm² 设计。加长压接可解决电气，但会推近定温边界 ⇒ 热学代价。
            if (args.Contains("--clamplen"))
            {
                double wallC = 0.4, insC = 10.0, holeC = wallC + 25.0;
                double discC = 30.0, tabLC = 50.0, tabWC = 20.0;
                var thC = new[] { 0.516, 0.855, 0.776, 0.426 };

                Console.WriteLine("=== 铜排压接长度：电气需求 vs 热学代价 ===");
                Console.WriteLine($"舌片宽 {2 * tabWC:0} mm，共用片电流 1099 A");
                Console.WriteLine();
                Console.WriteLine("① 电气侧：接触面积与界面电流密度");
                Console.WriteLine($"{"压接长 mm",11}{"单面接触 mm²",14}{"界面 J 单面",13}{"双面夹 J",11}  评价");
                foreach (double L in new[] { 3.0, 10.0, 20.0, 30.0, 40.0 })
                {
                    double a = L * 2 * tabWC;
                    Console.WriteLine($"{L,11:0}{a,14:0}{1099 / a,13:0.00}{1099 / (2 * a),11:0.00}  " +
                        (1099 / (2 * a) <= 1.0 ? "✓ 双面夹可满足 ≤1" :
                         1099 / (2 * a) <= 2.0 ? "⚠ 偏高" : "✗ 远超"));
                }
                Console.WriteLine("  （铜排压接接头一般按界面电流密度 ≤1 A/mm² 量级设计）");
                Console.WriteLine();

                Console.WriteLine("② 热学侧：加长压接 ⇒ 定温边界推向圆盘 ⇒ 铜排带走的热变化");
                Console.WriteLine($"{"压接长 mm",11}{"舌片有效长",12}{"入口 W",9}{"共用1 W",10}" +
                                  $"{"共用2 W",10}{"出口 W",9}{"合计 W",9}{"最差ΔT K",11}  收敛");
                foreach (double L in new[] { 3.0, 10.0, 20.0, 30.0 })
                {
                    var pC = SegmentSolver.Clone(p);
                    pC.Layer1.ThicknessMm = insC; pC.Layer1.Enabled = true;
                    pC.WallMinMm = wallC;
                    pC.FlangeInsulThickMm = 20; pC.FlangeInsulated = true;
                    pC.BusbarClampTempC = 300;
                    pC.BusbarClampLengthMm = L;

                    FlangePlate MkC(double t) => new()
                    {
                        DiscRadiusMm = discC, HoleRadiusMm = holeC,
                        TabEndXMm = -tabLC, TabEndHalfWidthMm = tabWC,
                        ThicknessMm = t, ThickenedMm = t, InsulBoundaryXMm = -1e9
                    };
                    var lcC = new LineCase
                    {
                        Base = pC, WallMm = wallC, UseMeasuredCurrent = false, CheckRamp = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = thC.Select(MkC).ToArray()
                    };
                    LineResult rC;
                    try { rC = LineRunner.Run(lcC); } catch (Exception ex)
                    { Console.WriteLine($"{L,11:0}   失败 {ex.Message}"); continue; }
                    if (!rC.Ok) { Console.WriteLine($"{L,11:0}   {rC.Message}"); continue; }
                    double worstD = rC.Segments.Max(x => Math.Abs(x.RootDeltaK));
                    Console.WriteLine($"{L,11:0}{tabLC - L,12:0}" +
                        string.Join("", rC.Flanges.Select(f4 => $"{f4.QClampW,9:0} ")) +
                        $"{rC.Flanges.Sum(f4 => f4.QClampW),8:0}{worstD,11:0.0}  " +
                        (rC.Converged ? "✓" : "✗"));
                }
                Console.WriteLine();
                Console.WriteLine("★ 两侧要一起看：压接太短电气做不出来，太长则热学上把法兰抽凉、管根温差变大。");
                Console.WriteLine("  若两者兼顾不了，出路是**加长舌片**（把压接区往外挪，导热路径不缩短）。");
                return;
            }

            // --cli --localstab   ★ 局部热失稳，含**升温全程**（用户 2026-08-13 二次澄清）
            //
            // 「局部温度提高 → 电阻提高 → 功率在该处堆 → 烧断，升温过程也会发生」。
            // 沿升温轨迹逐点判 J_stab vs 实际 J_max。
            if (args.Contains("--localstab"))
            {
                double wallL = 0.4, insL = 10.0, holeL = wallL + 25.0;
                double discL = 30.0, tabLL = 50.0, tabWL = 20.0;
                double tShared = 0.855, flInsL = 20.0;
                double lateral = discL - holeL;            // 热点到管孔的距离 = 环宽

                var pL = SegmentSolver.Clone(p);
                pL.Layer1.ThicknessMm = insL; pL.Layer1.Enabled = true;
                pL.WallMinMm = wallL;
                pL.FlangeInsulThickMm = flInsL; pL.FlangeInsulated = true;
                pL.BusbarClampTempC = 300;

                // 形状因子：J_max = ShapeJ·I/t（一次壳电流场即可，之后全解析）
                var gL = new FlangePlate
                {
                    DiscRadiusMm = discL, HoleRadiusMm = holeL,
                    TabEndXMm = -tabLL, TabEndHalfWidthMm = tabWL,
                    ThicknessMm = tShared, ThickenedMm = tShared, InsulBoundaryXMm = -1e9
                };
                var mL = FlangeMesher.Build(gL, 0, 2.0, 11.0, 45.0);
                var sfL = DesignScreen.Extract(mL, 1000.0, 1050.0, gL.Tangent().X);

                Console.WriteLine("=== 局部热失稳：升温全程 + 稳态 ===");
                Console.WriteLine("判据 ρe·J²·t·TCR  <  2·dq″/dT + k·t/L²　（左=加热的温度导数，右=散热的）");
                Console.WriteLine($"共用片 t={tShared:0.000} mm，热点到管孔 L={lateral:0.0} mm，法兰保温 {flInsL:0.0} mm");
                Console.WriteLine($"形状因子 ΣJ={sfL.ShapeJ:0.0000} ⇒ J_max = ΣJ·I/t");
                Console.WriteLine();
                Console.WriteLine($"{"管温 °C",9}{"TCR /K",11}{"段电流 A",10}{"J_max",9}" +
                                  $"{"加热 dP/dT",12}{"散热 表面",11}{"横向",10}{"J_stab",9}{"裕度",8}  判定");

                foreach (double tC in new[] { 100.0, 200.0, 400.0, 600.0, 800.0, 1000.0, 1150.0 })
                {
                    double iSeg = RampTwoNode.QuasiStaticCurrentA(pL, wallL, tC, 20.0);
                    double iPlate = Math.Sqrt(3.0) * iSeg;           // 共用片
                    double jmax = sfL.ShapeJ * iPlate / tShared;
                    var q = LocalStability.Check(pL, tC, jmax, tShared, flInsL, lateral);
                    Console.WriteLine($"{tC,9:0}{Materials.PtTcr(tC),11:0.00e+0}{iSeg,10:0}{jmax,9:0.00}" +
                        $"{q.HeatDeriv,12:0}{q.CoolSurf,11:0.0}{q.CoolLateral,10:0}" +
                        $"{q.JStab,9:0.00}{q.Margin,8:0.00}  " + (q.Stable ? "✓" : "★ 失稳"));
                }

                Console.WriteLine();
                Console.WriteLine("── 若**不计横向导热**（盘很宽、热点远离管子时的极限）");
                Console.WriteLine($"{"管温 °C",9}{"J_max",9}{"J_stab",9}{"裕度",8}  判定");
                foreach (double tC in new[] { 100.0, 400.0, 800.0, 1150.0 })
                {
                    double iSeg = RampTwoNode.QuasiStaticCurrentA(pL, wallL, tC, 20.0);
                    double jmax = sfL.ShapeJ * Math.Sqrt(3.0) * iSeg / tShared;
                    var q = LocalStability.Check(pL, tC, jmax, tShared, flInsL, double.NaN);
                    Console.WriteLine($"{tC,9:0}{jmax,9:0.00}{q.JStab,9:0.00}{q.Margin,8:0.00}  " +
                                      (q.Stable ? "✓" : "★ 失稳"));
                }
                Console.WriteLine();
                Console.WriteLine("★ TCR 随温度**下降而升高**（25 °C 是 1150 °C 的 6.5 倍）⇒ 冷态正反馈更强；");
                Console.WriteLine("  同时冷态 q″ 与 dq″/dT 趋近于零（辐射 ∝T⁴）。两头夹击 ⇒ 升温初段最危险。");
                Console.WriteLine("  横向导热是本方案的主稳定器：环宽仅 4.6 mm，k·t/L² 很大。盘一宽就没了。");
                return;
            }

            // --cli --flangestab   ★ 法兰热稳定：保温过头会不会失控（用户 2026-08-13 澄清的机理）
            //
            // 判据 dQ_散热/dT > dP_发热/dT。扫法兰保温厚度，看交付方案的 20 mm 是否越界。
            if (args.Contains("--flangestab"))
            {
                double wallB = 0.4, insB = 10.0, holeB = wallB + 25.0;
                double discB = 30.0, tabLB = 50.0, tabWB = 20.0;
                var thB = new[] { 0.516, 0.855, 0.776, 0.426 };

                Console.WriteLine("=== 法兰热稳定判据 ===");
                Console.WriteLine("机理：保温过头 → 温度↑ → 电阻↑ → 发热 P=I²R ↑ → 温度更↑ → 烧断");
                Console.WriteLine("判据：dQ_散热/dT > dP_发热/dT　（与净热流方向无关，是**稳定性**不是平衡）");
                Console.WriteLine();
                Console.WriteLine($"{"法兰保温 mm",12}{"片",10}{"发热 W",9}{"dP/dT",9}" +
                                  $"{"dQ/dT 表面",12}{"舌片",8}{"管孔",8}{"合计",8}{"裕度",8}  判定");

                foreach (double fi in new[] { 0.0, 2.5, 5.0, 10.0, 20.0, 40.0 })
                {
                    var pB = SegmentSolver.Clone(p);
                    pB.Layer1.ThicknessMm = insB; pB.Layer1.Enabled = true;
                    pB.WallMinMm = wallB;
                    pB.FlangeInsulThickMm = fi; pB.FlangeInsulated = fi > 1e-6;
                    pB.BusbarClampTempC = 300;

                    FlangePlate MkB(double t) => new()
                    {
                        DiscRadiusMm = discB, HoleRadiusMm = holeB,
                        TabEndXMm = -tabLB, TabEndHalfWidthMm = tabWB,
                        ThicknessMm = t, ThickenedMm = t,
                        InsulBoundaryXMm = fi > 1e-6 ? -1e9 : 1e9
                    };
                    var lcB = new LineCase
                    {
                        Base = pB, WallMm = wallB, UseMeasuredCurrent = false, CheckRamp = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = thB.Select(MkB).ToArray()
                    };
                    LineResult rB;
                    try { rB = LineRunner.Run(lcB); } catch (Exception ex)
                    { Console.WriteLine($"{fi,12:0.0}   求解失败 {ex.Message}"); continue; }
                    if (!rB.Ok) { Console.WriteLine($"{fi,12:0.0}   {rB.Message}"); continue; }

                    // 只报最不利的那片（发热最大的共用片）
                    int worst = 0;
                    for (int j2 = 1; j2 < rB.Flanges.Length; j2++)
                        if (rB.Flanges[j2].QGenW > rB.Flanges[worst].QGenW) worst = j2;
                    var f3 = rB.Flanges[worst];
                    double tPlate = f3.TRootC;      // 工作温度，不是可能已发散的片温
                    double tThick = thB[Math.Min(worst, thB.Length - 1)];
                    double aTotal = f3.AreaMm2;
                    double aIns = fi > 1e-6 ? aTotal : 0, aBare = fi > 1e-6 ? 0 : aTotal;

                    var st = FlangeStability.Check(pB, f3.QGenW, tPlate, aIns, aBare, fi,
                                 2 * tabWB * tThick, tabLB,
                                 2 * Math.PI * holeB * tThick, discB - holeB);
                    Console.WriteLine($"{fi,12:0.0}{f3.Name,10}{f3.QGenW,9:0}{st.DGenDT,9:0.000}" +
                        $"{st.DSurfDT,12:0.000}{st.DClampDT,8:0.000}{st.DTubeDT,8:0.000}" +
                        $"{st.DLossDT,8:0.000}{st.Margin,8:0.00}  " +
                        (st.Stable ? "✓ 稳定" : "★ 热失控"));
                }
                // ── 对照：**现役几何**（Ø120 + 200mm 舌 + 2mm 厚 + 纤维 2.5mm）
                //    这是验证本判据的关键 —— 现场确实烧过，模型能否复现？
                Console.WriteLine();
                Console.WriteLine("── 对照：现役几何（Ø120 / 舌200 / 厚2.0 / 管纤维2.5 / 无夹冷）");
                {
                    var pO = SegmentSolver.Clone(p);
                    pO.Layer1.ThicknessMm = 2.5; pO.Layer1.Enabled = true;
                    pO.WallMinMm = 1.0;
                    pO.FlangeInsulThickMm = 2.5; pO.FlangeInsulated = true;
                    pO.BusbarClampTempC = -1;                 // 现役无夹冷
                    FlangePlate MkO(double t) => new()
                    {
                        DiscRadiusMm = 60, HoleRadiusMm = 26,
                        TabEndXMm = -200, TabEndHalfWidthMm = 40,
                        ThicknessMm = t, ThickenedMm = t,
                        InsulBoundaryXMm = double.NaN        // 仅圆盘包，舌片裸露（现场实况）
                    };
                    var lcO = new LineCase
                    {
                        Base = pO, WallMm = 1.0, UseMeasuredCurrent = false, CheckRamp = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = new[] { MkO(2.0), MkO(2.0), MkO(2.0), MkO(2.0) }
                    };
                    try
                    {
                        var rO = LineRunner.Run(lcO);
                        if (rO.Ok)
                        {
                            int w2 = 0;
                            for (int j3 = 1; j3 < rO.Flanges.Length; j3++)
                                if (rO.Flanges[j3].QGenW > rO.Flanges[w2].QGenW) w2 = j3;
                            var fO = rO.Flanges[w2];
                            // ★ 在**工作温度**评，不是发散后的片温
                            var stO = FlangeStability.Check(pO, fO.QGenW, fO.TRootC,
                                          fO.AreaMm2 * 0.21, fO.AreaMm2 * 0.79, 2.5,
                                          2 * 40 * 2.0, 200,
                                          2 * Math.PI * 26 * 2.0, 60 - 26);
                            Console.WriteLine($"{"（现役）",12}{fO.Name,10}{fO.QGenW,9:0}{stO.DGenDT,9:0.000}" +
                                $"{stO.DSurfDT,12:0.000}{stO.DClampDT,8:0.000}{stO.DTubeDT,8:0.000}" +
                                $"{stO.DLossDT,8:0.000}{stO.Margin,8:0.00}  " +
                                (stO.Undetermined ? "? 判不了" : stO.Stable ? "✓ 稳定" : "★ 热失控"));
                            if (stO.Undetermined) Console.WriteLine("            " + stO.Note);
                            Console.WriteLine($"{"",12}片最高温 {fO.TMaxC:0} °C　管孔导热路径长 34 mm" +
                                              $"（交付方案仅 4.6 mm）");
                        }
                        else Console.WriteLine("　现役几何求解失败：" + rO.Message);
                    }
                    catch (Exception ex) { Console.WriteLine("　现役几何异常：" + ex.Message); }
                }

                Console.WriteLine();
                Console.WriteLine("裕度 = dQ/dT ÷ dP/dT，**必须 > 1**。");
                Console.WriteLine("读法：保温越厚，表面那一项越小 ⇒ 裕度下降。");
                Console.WriteLine("舌片与管孔两项与保温无关，是稳定器 —— 它们撑不住时，加保温就会失控。");
                return;
            }

            // --cli --gradetest   ★ 分级到底有没有用：扫「梯度比 γ」
            //
            // 用户问得对：等厚的最优（1327 g）早算过了，重算等厚没有新信息。
            // 真问题是**三级厚度各自不同能不能更轻**。
            //
            // 把厚度分布用一个参数 γ 表示：t(r) ∝ (r/r_out)^(−γ)
            //   γ = 0   等厚（现有最优就是这一档）
            //   γ > 0   孔周厚、外缘薄（补偿 J 在孔周的峰值）
            //   γ < 0   反过来
            // 每个 γ 各自做自动定厚（整体标度由 C2 定），再比总铂。
            // **若最小值出现在 γ≠0，分级就是有用的；若就在 γ=0，分级白搭。**
            if (args.Contains("--gradetest"))
            {
                double wallG = 0.4, insG = 10.0, holeG = wallG + 25.0;
                double discG = 30.0, tabLG = 50.0, tabWG = 20.0;   // 现有最优的形状
                int nLv = 3;

                var pG = SegmentSolver.Clone(p);
                pG.Layer1.ThicknessMm = insG; pG.Layer1.Enabled = true;
                pG.WallMinMm = wallG;
                pG.FlangeInsulThickMm = 20; pG.FlangeInsulated = true;
                pG.BusbarClampTempC = 300;

                Console.WriteLine("=== 分级有没有用：扫梯度比 γ ===");
                Console.WriteLine($"形状固定为现有最优 Ø{2 * discG:0}／舌{tabLG:0}×{tabWG:0}，孔 R{holeG:0.0}");
                Console.WriteLine($"圆盘在 R{holeG:0.0}–{discG:0} 之间分 {nLv} 级，厚度 t ∝ (r/r_out)^(−γ)");
                Console.WriteLine("γ=0 即等厚（= 已知的 1327 g 那档）");
                Console.WriteLine();
                Console.WriteLine($"{"γ",7}{"各级厚度比",22}{"四片基准厚 mm",26}" +
                                  $"{"minΔT",8}{"maxΔT",8}{"法兰最高",10}{"总铂 g",9}  判定");

                foreach (double g in new[] { -0.5, 0.0, 0.5, 1.0, 1.5, 2.0 })
                {
                    // 各级中点半径 → 相对厚度（归一化到几何平均 1，保证 γ 只改分布不改总量）
                    var rmid = new double[nLv];
                    var rad = new double[nLv];
                    for (int m = 0; m < nLv; m++)
                    {
                        double a = holeG + (discG - holeG) * m / nLv;
                        double b = holeG + (discG - holeG) * (m + 1) / nLv;
                        rmid[m] = 0.5 * (a + b); rad[m] = b;
                    }
                    var rel = rmid.Select(r => Math.Pow(r / discG, -g)).ToArray();
                    double gm = Math.Exp(rel.Select(v => Math.Log(v)).Average());
                    for (int m = 0; m < nLv; m++) rel[m] /= gm;

                    FlangePlate MkG(double t) => new()
                    {
                        DiscRadiusMm = discG, HoleRadiusMm = holeG,
                        TabEndXMm = -tabLG, TabEndHalfWidthMm = tabWG,
                        ThicknessMm = t, ThickenedMm = t, InsulBoundaryXMm = -1e9,
                        DiscStepRadiiMm = rad.Take(nLv - 1).ToArray(),
                        DiscStepThicknessMm = rel.Take(nLv - 1).Select(v => v * t).ToArray()
                    };
                    var lcG = new LineCase
                    {
                        Base = pG, WallMm = wallG, UseMeasuredCurrent = false, CheckRamp = false,
                        SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                        FlangePlates = new[] { MkG(0.5), MkG(0.9), MkG(0.8), MkG(0.45) }
                    };
                    var rG = FlangeAutoSizer.SolveAuto(lcG, MkG, new[] { 0.5, 0.9, 0.8, 0.45 },
                                new FlangeAutoSizer.Options(),
                                new SyncProgress<string>(_ => { }), default);
                    if (rG.Line is not { Ok: true } lr) { Console.WriteLine($"{g,7:0.0}   求解失败"); continue; }
                    double dmin = lr.Segments.Min(x => x.RootDeltaK);
                    double dmax = lr.Segments.Max(x => x.RootDeltaK);
                    double tmax = lr.Flanges.Max(f2 => f2.TMaxC);
                    bool ok = rG.Converged && lr.Converged && dmin > 0 && dmax <= 10
                              && tmax <= RampTwoNode.PtMeltingC - 200;
                    Console.WriteLine($"{g,7:0.0}{string.Join(":", rel.Select(v => v.ToString("0.00"))),22}" +
                        $"{string.Join("/", rG.ThicknessMm.Select(v => v.ToString("0.000"))),26}" +
                        $"{dmin,8:+0.0;-0.0}{dmax,8:+0.0;-0.0}{tmax,10:0}{lr.TotalMassG,9:0}  " +
                        (ok ? "✓" : "✗"));
                }
                Console.WriteLine();
                Console.WriteLine("读法：最小总铂若出现在 γ≠0，分级有用；若就在 γ=0，分级白搭。");
                Console.WriteLine("（γ>0 = 孔周厚外缘薄，用来补偿 J 在孔周的峰值）");
                return;
            }

            // --cli --stepsearch   ★ 三级阶梯法兰的几何搜索（用户「走 2：缩小法兰」）
            //
            // 自由度：三级半径 R1<R2<R3、三级厚度 t1..t3、舌片长度与末端半宽。
            // 走**解析几何**（FlangePlate 的 DiscStepRadii/DiscStepThickness），
            // 不经 Rhino，故单点远快于 .3dm 路径；选出的形状再由 Geom steps 出图复核。
            //
            // 判据同总纲：0 < 管根温差 < 10 K（C2）、法兰局部不超管温太多（C1）、总铂最小。
            if (args.Contains("--stepsearch"))
            {
                double wallS = 0.4, insS = 10.0;
                var setpS = new[] { 1150.0, 1080.0, 1050.0 };
                double holeR = wallS + 25.0;

                var pS = SegmentSolver.Clone(p);
                pS.Layer1.ThicknessMm = insS; pS.Layer1.Enabled = true;
                pS.WallMinMm = wallS;
                pS.FlangeInsulThickMm = 20; pS.FlangeInsulated = true;
                pS.BusbarClampTempC = 300;

                Console.WriteLine("=== 三级阶梯法兰 几何搜索（缩小法兰）===");
                Console.WriteLine($"管壁 {wallS:0.0} / 管纤维 {insS:0.0} / 法兰全包 20 / 夹持 300 °C　孔 R{holeR:0.0}");
                Console.WriteLine("三级厚度由「自动定厚」在每个形状上自行决定（全放开，不锁级）");
                Console.WriteLine();
                Console.WriteLine($"{"R1/R2/R3",14}{"舌长",7}{"舌半宽",8}{"厚度 t1/t2/t3",20}" +
                                  $"{"minΔT",8}{"maxΔT",8}{"法兰最高",10}{"总铂 g",9}  判定");

                var best = (mass: double.MaxValue, desc: "", th: Array.Empty<double>());
                foreach (double r3 in new[] { 34.0, 40.0, 48.0 })
                    foreach (double tabL in new[] { 60.0, 100.0 })
                        foreach (double tabW in new[] { 15.0, 25.0 })
                        {
                            double r1 = holeR + (r3 - holeR) / 3.0;
                            double r2 = holeR + (r3 - holeR) * 2.0 / 3.0;
                            if (r3 >= Math.Sqrt(tabL * tabL + tabW * tabW)) continue;

                            // 三级厚度：以一个基准 t 乘固定比例，由自动定厚求基准
                            FlangePlate MkS(double t) => new()
                            {
                                DiscRadiusMm = r3, HoleRadiusMm = holeR,
                                TabEndXMm = -tabL, TabEndHalfWidthMm = tabW,
                                ThicknessMm = t, ThickenedMm = t, InsulBoundaryXMm = -1e9,
                                DiscStepRadiiMm = new[] { r1, r2 },
                                DiscStepThicknessMm = new[] { t, t }   // 起点等厚，比例由内层调
                            };
                            var lcS = new LineCase
                            {
                                Base = pS, WallMm = wallS, UseMeasuredCurrent = false, CheckRamp = false,
                                SetpointC = setpS,
                                FlangePlates = new[] { MkS(0.6), MkS(0.8), MkS(0.8), MkS(0.6) }
                            };
                            var rS = FlangeAutoSizer.SolveAuto(lcS, MkS, new[] { 0.6, 0.8, 0.8, 0.6 },
                                        new FlangeAutoSizer.Options(),
                                        new SyncProgress<string>(_ => { }), default);
                            if (rS.Line is not { Ok: true } lr) continue;
                            double dmin = lr.Segments.Min(x => x.RootDeltaK);
                            double dmax = lr.Segments.Max(x => x.RootDeltaK);
                            double tmax = lr.Flanges.Max(f2 => f2.TMaxC);
                            bool ok = rS.Converged && lr.Converged && dmin > 0 && dmax <= 10
                                      && tmax <= RampTwoNode.PtMeltingC - 200;
                            Console.WriteLine($"{$"{r1:0}/{r2:0}/{r3:0}",14}{tabL,7:0}{tabW,8:0}" +
                                $"{string.Join("/", rS.ThicknessMm.Select(x => x.ToString("0.00"))),20}" +
                                $"{dmin,8:+0.0;-0.0}{dmax,8:+0.0;-0.0}{tmax,10:0}{lr.TotalMassG,9:0}  " +
                                (ok ? "✓" : (dmax > 10 || dmin <= 0 ? "✗C2" : "") +
                                            (tmax > RampTwoNode.PtMeltingC - 200 ? "✗熔点" : "")));
                            if (ok && lr.TotalMassG < best.mass)
                                best = (lr.TotalMassG, $"R{r1:0}/{r2:0}/{r3:0}　舌{tabL:0}×{tabW:0}",
                                        rS.ThicknessMm);
                        }

                Console.WriteLine();
                if (best.mass < double.MaxValue)
                {
                    Console.WriteLine($"★ 最优：{best.desc}　厚度 " +
                        string.Join("/", best.th.Select(x => x.ToString("0.000"))) +
                        $" mm　整线总铂 {best.mass:0} g（现状 7141 g，省 {(7141 - best.mass) / 7141 * 100:0.0} %）");
                }
                else Console.WriteLine("✗ 本轮形状库内无可行解 —— 需继续缩小或改电流。");
                return;
            }

            // --cli --leveltest <file.3dm> [图层]   逐级定厚试算：全放开 vs 锁外圈
            //
            // 「外圈厚度不动、只调内圈」到底管不管用，用同一张图跑两遍对比。
            if (args.Contains("--leveltest"))
            {
                int lti = Array.IndexOf(args, "--leveltest");
                string ltf = lti + 1 < args.Length && !args[lti + 1].StartsWith("--")
                             ? args[lti + 1] : Find3dm("Pt_Heater.3dm");
                string ltl = lti + 2 < args.Length && !args[lti + 2].StartsWith("--")
                             ? args[lti + 2] : "法兰";

                Console.WriteLine("=== 逐级定厚试算 ===");
                var fld = Geometry3dm.LoadThickness(ltf, ltl, double.NaN, 0.5);
                var shp = PlateShapeAnalyzer.Analyze(fld);
                Console.WriteLine(PlateShapeAnalyzer.Format(shp));
                int L = shp.Levels.Count;
                if (L < 2) { Console.WriteLine("只有一级，无从逐级调。"); return; }

                var lvT = Enumerable.Range(0, 4)
                            .Select(_ => shp.Levels.Select(x => x.ThicknessMm).ToArray()).ToArray();

                var pf = SegmentSolver.Clone(p);
                pf.Layer1.ThicknessMm = 10; pf.Layer1.Enabled = true;
                pf.WallMinMm = 0.4;
                pf.FlangeInsulThickMm = 20; pf.FlangeInsulated = true;
                pf.BusbarClampTempC = 300;

                var lc = new LineCase
                {
                    Base = pf, WallMm = 0.4, UseMeasuredCurrent = false, CheckRamp = false,
                    SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                    FlangeFile3dm = Enumerable.Repeat(ltf, 4).ToArray(),
                    FlangeLayer = ltl
                };

                // 外圈 = 半径最大那一级
                int outer = 0;
                for (int m = 1; m < L; m++)
                    if (shp.Levels[m].ROuterMm > shp.Levels[outer].ROuterMm) outer = m;

                foreach (var (tag, mask) in new (string, bool[][]?)[]
                {
                    ("全放开（优化器自定各级比例）", null),
                    ($"锁第{outer + 1}级（外圈 t={shp.Levels[outer].ThicknessMm:0.00}）不动",
                     Enumerable.Range(0, 4).Select(_ =>
                        Enumerable.Range(0, L).Select(m => m == outer).ToArray()).ToArray()),
                })
                {
                    Console.WriteLine();
                    Console.WriteLine("── " + tag);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var rr2 = FlangeAutoSizer.SolveByLevel(lc, lvT, new FlangeAutoSizer.Options(),
                                new SyncProgress<string>(_ => { }), default, 5, mask);
                    sw.Stop();
                    Console.WriteLine($"   用时 {sw.Elapsed.TotalMinutes:0.0} min　" +
                                      (rr2.Converged ? "✓ 收敛" : "✗ 未收敛"));
                    if (rr2.Line is { } lr && lr.Ok)
                    {
                        Console.WriteLine($"   段温差 " + string.Join(" / ",
                            lr.Segments.Select(x => x.RootDeltaK.ToString("+0.0;-0.0"))) + " K");
                        Console.WriteLine($"   法兰最高 {lr.Flanges.Max(f2 => f2.TMaxC):0} °C　" +
                                          $"总铂 {lr.TotalMassG:0} g　省 {lr.SavingPct:0.0} %");
                        var f0 = lr.Flanges[1];
                        if (f0.LevelThickMm.Length > 0)
                            Console.WriteLine("   共用片各级 厚度/峰值：" + string.Join("　",
                                Enumerable.Range(0, f0.LevelThickMm.Length).Select(m =>
                                    $"{f0.LevelThickMm[m]:0.000}mm/{f0.LevelTMaxC[m]:0}°C")));
                    }
                    Console.WriteLine("   " + rr2.Message);
                }
                return;
            }

            // --cli --shapevars <file.3dm> [图层]   从 .3dm 反推法兰的几何变数
            //
            // 「你给形状，能不能分析出有哪些几何变数」的实现。厚度场里已含全部信息
            // （t=0 表示无材料），故轮廓、孔、槽、阶梯一次全部读出。
            // 有了参数，任意形状才谈得上进优化。
            if (args.Contains("--shapevars"))
            {
                int svi = Array.IndexOf(args, "--shapevars");
                string svf = svi + 1 < args.Length && !args[svi + 1].StartsWith("--")
                             ? args[svi + 1] : Find3dm("Pt_Heater.3dm");
                string svl = svi + 2 < args.Length && !args[svi + 2].StartsWith("--")
                             ? args[svi + 2] : "法兰";
                Console.WriteLine($"读取 {Path.GetFileName(svf)}　图层「{svl}」…");
                try
                {
                    var fld = Geometry3dm.LoadThickness(svf, svl, double.NaN, 0.5);
                    Console.WriteLine(PlateShapeAnalyzer.Format(PlateShapeAnalyzer.Analyze(fld)));
                }
                catch (Exception ex) { Console.WriteLine("✗ " + ex.Message); }
                return;
            }

            // --cli --gate1   第一步：能不能升到目标温度（管侧）
            //
            // ★ 顺序很重要（用户 2026-08-11 纠正）：**升温到目标温度是第一步，
            //   完成后才有稳态计算**。所以先过这一关，再谈稳态的 ΔT<10，最后才谈省铂。
            //
            // 温控功率下升温是准静态的，所以「能不能到 1150」就等于
            // 「1150 °C 的稳态工作点存不存在、要多大电流」：
            //     P = Q_散热(1150)，  I = √(P/R)，  J = I/A
            // 管壁越薄 ⇒ A 小、R 大 ⇒ 电流小但 **J ∝ 1/√t 反而高**。
            // 保温越厚 ⇒ Q_散热 小 ⇒ 电流与 J 一起降 —— 用户已确认保温无空间限制，
            // 所以这是管侧最有力的免费杠杆。
            if (args.Contains("--gate1"))
            {
                double tTarget = 1150;
                Console.WriteLine("=== 第一步：能不能升到目标温度（管侧）===");
                Console.WriteLine($"目标 {tTarget:0} °C，段长 {p.TubeLengthMm:0} mm，内径 {p.TubeIdMm:0} mm");
                Console.WriteLine("温控功率下升温是准静态的 ⇒「能不能到」= 该温度的稳态工作点要多大电流");
                Console.WriteLine("（空管口径：升温时管内无玻璃，故不含玻璃换热项）");
                Console.WriteLine();

                Console.WriteLine($"{"纤维 mm",9}{"管壁 mm",9}{"散热 W",9}{"电流 A",9}{"管 J",8}" +
                                  $"{"I_stab A",10}{"稳定裕度",10}{"管铂 g/段",11}  判定");

                foreach (double ins in new[] { 2.5, 5.0, 10.0, 20.0, 40.0 })
                {
                    foreach (double w in new[] { 0.4, 0.6, 0.8, 1.0 })
                    {
                        var q = SegmentSolver.Clone(p);
                        q.Layer1.ThicknessMm = ins; q.Layer1.Enabled = true;
                        q.WallMinMm = w; q.TSetC = tTarget;

                        double ri = q.TubeIdMm * 0.5e-3, ww = w * 1e-3, rOut = ri + ww;
                        double areaM2 = Math.PI * (rOut * rOut - ri * ri);
                        double areaMm2 = areaM2 * 1e6;

                        bool anyIns = false;
                        foreach (var l in q.Layers) if (l.Enabled && l.ThicknessMm > 1e-6) anyIns = true;
                        double eps = anyIns ? q.OuterEmissivity : q.PtEmissivity;

                        var tab = new LossTable(q.TAmbC, tTarget + 300, 60,
                            t => Insulation.CylinderLoss(t, q.TAmbC, rOut, q.Layers, eps,
                                     q.Posture == PtOptimize.Core.Orientation.Vertical, q.TubeLength,
                                     q.LossScale).QPerLength);

                        double lossW = tab.Eval(tTarget) * q.TubeLength;
                        double rOhm = Materials.PtResistivity(tTarget) * q.TubeLength / areaM2;
                        double iA = Math.Sqrt(lossW / rOhm);
                        double jA = iA / areaMm2;

                        // 空管热稳定极限：β 不含玻璃项
                        double beta = tab.Slope(tTarget);
                        double drhoDt = Materials.RhoRef *
                                        (Materials.AlphaFit + 2 * Materials.BetaFit * tTarget);
                        double iStab = Math.Sqrt(Math.Max(1e-9, beta * areaM2 / drhoDt));
                        double margin = iStab / Math.Max(1e-9, iA);
                        double massG = areaMm2 * q.TubeLengthMm * Materials.PtDensity * 1e-6;

                        string v = margin > 1.5 ? "✓" : margin > 1.0 ? "⚠ 裕度薄" : "✗ 越热稳定极限";
                        Console.WriteLine($"{ins,9:0.0}{w,9:0.0}{lossW,9:0}{iA,9:0}{jA,8:0.00}" +
                                          $"{iStab,10:0}{margin,10:0.00}{massG,11:0}  {v}");
                    }
                    Console.WriteLine();
                }

                Console.WriteLine("读法：");
                Console.WriteLine("· **管 J ∝ 1/√管壁** —— 减薄管壁并不减少电流负担，反而抬高 J。");
                Console.WriteLine("· **加厚保温同时降电流与 J**，且保温无空间限制 ⇒ 管侧的免费杠杆。");
                Console.WriteLine("· 热稳定极限 I_stab = √(βA/(dρe/dT))：越过它稳态解本就不存在（§7）。");
                Console.WriteLine("· 本表是第一步的**通过性**，省铂的取舍要等第二步（稳态 ΔT<10）一起看。");
                return;
            }

            // --cli --gate2   第二步：稳态 ΔT<10 ⇒ 法兰必须热自给 ⇒ 法兰质量的闭式
            //
            // C2 的抽热预算只有 ~10 W，而法兰的发热与散热各是几百瓦 ⇒ 必须 Φ ≈ 1。
            // 把 Φ = 1 写开：
            //     f²I²·(ρe·ShapeR/t) = 2·A·q″     （左=自身发热，右=自身散热）
            //   ⇒ t = f²I²·ρe·ShapeR / (2·A·q″)
            //   ⇒ m = A·t·ρ_Pt = **f²·I²·ρe·ShapeR·ρ_Pt / (2·q″)**
            //
            // ★ 面积 A 约掉了 —— 自给法兰的质量**与盘面大小无关**，只由四个量定：
            //     f²（接线相位）· I²（段电流）· ShapeR（电流路径的形状数）· 1/q″（表面散热）
            // 这四个正好对应用户给的四个自由度，且都是乘性的。
            if (args.Contains("--gate2"))
            {
                Console.WriteLine("=== 第二步：稳态 ΔT<10 ⇒ 法兰热自给 ⇒ 质量闭式 ===");
                Console.WriteLine("Φ=1：f²I²·(ρe·ShapeR/t) = 2·A·q″  ⇒  m = f²·I²·ρe·ShapeR·ρ_Pt/(2q″)");
                Console.WriteLine("★ 面积约掉了：自给法兰的质量与盘面大小无关，只由 f²、I²、ShapeR、1/q″ 定");
                Console.WriteLine();

                var g0 = new FlangePlate();
                var mesh0 = FlangeMesher.Build(g0, 0, hFine: 2.0, hCoarse: 11.0, fineRadius: 45.0);
                var sf = DesignScreen.Extract(mesh0, 1000.0, 1050.0, g0.Tangent().X);
                // 注：这个切分是**保温分界**（切点 x）两侧，不是几何上的「圆盘 vs 舌片」——
                // 切点在 x=−6.07，圆盘的大半落在分界的裸露侧。
                // 几何口径：圆盘环面 π(60²−26²)=9185 mm²，舌片超出圆盘部分 = 总 − 圆盘 = 14356 mm²（61 %）。
                double discAnnulus = Math.PI * (g0.DiscRadiusMm * g0.DiscRadiusMm
                                              - g0.HoleRadiusMm * g0.HoleRadiusMm);
                Console.WriteLine($"现役形状（Ø120 + 200 mm 舌片）：净面积 {sf.AreaMm2:0} mm²");
                Console.WriteLine($"  几何切分：圆盘环面 {discAnnulus:0} / 舌片超出部分 {sf.AreaMm2 - discAnnulus:0}" +
                                  $"（舌片占 {(sf.AreaMm2 - discAnnulus) / sf.AreaMm2 * 100:0} %）");
                Console.WriteLine($"  保温切分（切点 x={g0.Tangent().X:0.0}）：包纤维侧 {sf.DiscAreaMm2:0} / 裸露侧 {sf.TabAreaMm2:0}");
                Console.WriteLine($"  电阻形状数 ShapeR = {sf.ShapeR:0.000}   " +
                                  $"J 形状数 ShapeJ = {sf.ShapeJ:0.0000} (A/mm² per A per mm)");
                Console.WriteLine();

                double tWork = 1150;
                double rhoMm = Materials.PtResistivity(tWork) * 1e3;         // Ω·mm

                double SelfSufficientMassG(double f, double iSeg, double shapeR,
                                           double qWPerMm2, out double tMm, out double areaNeed)
                {
                    // m = f²I²·ρe·ShapeR·ρ_Pt/(2q″)；面积随之定 A = f²I²ρe·ShapeR/(2 q″ t)
                    double m = f * f * iSeg * iSeg * rhoMm * shapeR * (Materials.PtDensity * 1e-6)
                               / (2.0 * qWPerMm2);
                    tMm = double.NaN; areaNeed = double.NaN;
                    return m;
                }

                double qBare = DesignScreen.PlateFluxWPerM2(p, tWork, 0) * 1e-6;      // W/mm²
                double qIns = DesignScreen.PlateFluxWPerM2(p, tWork, p.FlangeInsulThickMm) * 1e-6;

                Console.WriteLine($"表面热流 q″@{tWork:0} °C：裸露 {qBare * 1e6:0} W/m²   " +
                                  $"包 {p.FlangeInsulThickMm:0.0} mm 纤维 {qIns * 1e6:0} W/m²");
                Console.WriteLine();

                Console.WriteLine("── 单片自给质量（裸露口径）随「段电流」与「叠加系数」");
                Console.WriteLine($"{"段电流 A",10}{"端片 f=1",12}{"共用 f=1.24",14}{"共用 f=1.5",13}{"共用 f=√3",13}   [g]");
                foreach (double iSeg in new[] { 1655.0, 1364.0, 1100.0, 884.0, 719.0, 449.0 })
                {
                    double m1 = SelfSufficientMassG(1.0, iSeg, sf.ShapeR, qBare, out _, out _);
                    double m2 = SelfSufficientMassG(1.24, iSeg, sf.ShapeR, qBare, out _, out _);
                    double m3 = SelfSufficientMassG(1.5, iSeg, sf.ShapeR, qBare, out _, out _);
                    double m4 = SelfSufficientMassG(Math.Sqrt(3), iSeg, sf.ShapeR, qBare, out _, out _);
                    Console.WriteLine($"{iSeg,10:0}{m1,12:0}{m2,14:0}{m3,13:0}{m4,13:0}");
                }
                Console.WriteLine();
                Console.WriteLine("（段电流一列对应 --gate1 的保温档：2.5 mm→1655 A … 40 mm→719 A@壁1.0）");
                Console.WriteLine();

                Console.WriteLine("── 四个乘性杠杆各自的倍率（相对现状 f=√3、I=1655、裸露、现役形状）");
                double mBase = SelfSufficientMassG(Math.Sqrt(3), 1655, sf.ShapeR, qBare, out _, out _);
                Console.WriteLine($"  基准（现状工况下的自给质量）        {mBase,8:0} g/片");
                Console.WriteLine($"  ① 接线改同相 f √3→0.1              ×{0.1 * 0.1 / 3.0,7:0.0000}" +
                                  $"  ⇒ {mBase * 0.01 / 3.0,8:0.0} g");
                Console.WriteLine($"  ② 管保温 2.5→40 mm，I 1655→719 A   ×{719.0 * 719.0 / (1655.0 * 1655.0),7:0.000}" +
                                  $"  ⇒ {mBase * 719.0 * 719.0 / (1655.0 * 1655.0),8:0} g");
                Console.WriteLine($"  ③ 法兰包纤维（反向，变重）          ×{qBare / qIns,7:0.000}" +
                                  $"  ⇒ {mBase * qBare / qIns,8:0} g");
                Console.WriteLine($"  ④ ShapeR 减半（短而宽的电流路径）    ×{0.5,7:0.000}" +
                                  $"  ⇒ {mBase * 0.5,8:0} g");
                Console.WriteLine();
                Console.WriteLine("★ 设计规则由此直接读出：");
                Console.WriteLine("  · **最小化 ShapeR** = 电流路径要短而宽 ⇒ 舌片宜短、宜宽（不是加长）");
                Console.WriteLine("  · **最大化 q″** ⇒ 法兰不包保温；空冷还能再往上抬");
                Console.WriteLine("  · **降 I** ⇒ 管保温加厚（无空间限制，且同时省管子的铂）");
                Console.WriteLine("  · **降 f** ⇒ 改接线相位，这一项是平方且倍率最大");
                Console.WriteLine();
                Console.WriteLine("⚠ 但质量不能无限降：t ≥ 0.4 mm 的下界会先咬住 ——");
                Console.WriteLine("  t = f²I²ρe·ShapeR/(2·A·q″)，A 由形状定；t 撞到 0.4 后法兰就**过厚**，");
                Console.WriteLine("  Φ<1 ⇒ 开始从管子抽热 ⇒ C2 破。那时要靠缩小盘面（减 A）把 t 顶回去。");
                return;
            }

            // --cli --budget   C2 的「抽热预算」—— 先看清这条约束有多紧
            //
            // C2（管根温差 <10 K）经半无限翅片解可以反过来写成对法兰抽热 D 的硬预算：
            //   |D| ≤ ΔT_max · √(k·A_管·β)
            // 这个根号量只有 0.5 W/K 量级 ⇒ 预算只有几瓦，而现役片抽 236–394 W。
            // 先把这条摊开，因为它决定整个优化是「调参数」还是「换思路」。
            if (args.Contains("--budget"))
            {
                Console.WriteLine("=== C2 的抽热预算：管根温差 <10 K 允许法兰抽走多少热 ===");
                Console.WriteLine("判据 |ΔT_root| = D / √(k·A_管·β)  ⇒  |D| ≤ ΔT_max·√(k·A_管·β)");
                Console.WriteLine("（半无限翅片解，HANDOVER §6 ②；D = 法兰从管子抽走的净热）");
                Console.WriteLine();

                Console.WriteLine($"{"管壁 mm",9}{"管截面 mm²",12}{"√(kAβ) W/K",13}" +
                                  $"{"10K 预算 W",12}{"1K 预算 W",12}");
                foreach (double w in new[] { 0.4, 0.6, 0.8, 1.0, 1.5, 2.0, 3.0 })
                {
                    double b10 = DesignScreen.DrawBudgetW(p, w, 1150, 10.0);
                    double area = Math.PI * w * (p.TubeIdMm + w);
                    Console.WriteLine($"{w,9:0.0}{area,12:0.0}{b10 / 10,13:0.000}{b10,12:0.0}{b10 / 10,12:0.0}");
                }
                Console.WriteLine();
                double bud = DesignScreen.DrawBudgetW(p, 1.0, 1150, 10.0);
                Console.WriteLine("── 与收敛解对表（半无限翅片解是保守的，管子有限长且两端各一片）");
                Console.WriteLine("现役（--run 收敛解，管壁 1.0）：出口片抽热 +236 W ↔ HC3 管根温差 +230.5 K");
                Console.WriteLine($"  ⇒ 实际灵敏度 {230.5 / 236:0.00} K/W，而翅片式给 {10 / bud:0.00} K/W（保守 {(10 / bud) / (230.5 / 236):0.0} 倍）");
                Console.WriteLine($"  ⇒ 现实口径的 10 K 预算约 {10 / (230.5 / 236):0.0} W，翅片式给 {bud:0.0} W");
                Console.WriteLine();
                Console.WriteLine($"★★ 无论取哪个口径：现役抽热 236–394 W 是预算的 " +
                                  $"**{236 / (10 / (230.5 / 236)):0}–{394 / bud:0} 倍**。");
                Console.WriteLine();
                Console.WriteLine("★ 这条比 C1 紧得多：法兰必须做到**近乎完全热自给**（Φ 落在 1 附近很窄的带里）。");
                Console.WriteLine("  预算 ∝ √(A_管) ∝ √(管壁) —— **管壁越薄，容差越小**，");
                Console.WriteLine("  这就是「省铂」与「压温差」在管壁这个自由度上直接对冲的机理。");
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
                pp2.SizeWall = false; pp2.WallMinMm = 0.6;
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

                Save("profile_axial.png", f => UI.FieldPlots.DrawAxialProfile(f, r, p));
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
