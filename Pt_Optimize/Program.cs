using System.Linq;
using PtOptimize.Core;
using PtOptimize.UI;

namespace PtOptimize;

internal static class Program
{
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

    [STAThread]
    private static void Main(string[] args)
    {
        // 批处理: Pt_Optimize.exe --cli [case.json]
        if (args.Length > 0 && args[0] == "--cli")
        {
            var p = args.Length > 1 && !args[1].StartsWith("--")
                ? System.Text.Json.JsonSerializer.Deserialize<DesignInputs>(File.ReadAllText(args[1]))!
                : new DesignInputs();
            var r = SegmentSolver.Solve(p);
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* WinExe 无控制台 */ }
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

            // --cli --line   分段核算（示例三段，UI 里可编辑）
            if (args.Contains("--line"))
            {
                var segs = new List<Segment>
                {
                    new(){ Name="HC1", TSetC=1150, TGlassInC=1150, GlassHeadM=0.3, LengthMm=300, WallMm=1.0 },
                    new(){ Name="HC2", TSetC=1200, TGlassInC=1200, GlassHeadM=0.6, LengthMm=300, WallMm=1.0 },
                    new(){ Name="HC3", TSetC=1250, TGlassInC=1250, GlassHeadM=1.0, LengthMm=300, WallMm=1.0 },
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
                    Console.WriteLine($"合计 铂重 {m:0} g   相对成本 {c:0}" + (bad > 0 ? $"   ★{bad} 段超限" : ""));
                }

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
