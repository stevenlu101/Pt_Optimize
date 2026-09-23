using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R48 J 路（求解器与界面安全线，2026-09-15 Opus 5）：**改之前先做实验确证** 的慢探针。
/// 每条只印、不断言结论（断言留给快门）；输出只写带开跑时刻的新文件 deliverable/J路_{题}_本次开跑于{时刻}.txt，不覆盖已有证据。
/// 跑法：dotnet test --filter "FullyQualifiedName~R48J_ExperimentProbeTests.{方法名}"；拷贝出去的程序集跑时设环境变量 PT_J_REPO = 仓库根。
/// </summary>
public class R48J_ExperimentProbeTests
{
    internal static string Repo => Environment.GetEnvironmentVariable("PT_J_REPO") is { Length: > 0 } r ? r : HandoverDoc.Root();

    internal sealed class Probe
    {
        public readonly string Path;
        private readonly StringBuilder _sb = new();
        private readonly string _header;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public Probe(string topic, LineCase? lc = null)
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            Path = System.IO.Path.Combine(Repo, "deliverable", $"J路_{topic}_本次开跑于{stamp}.txt");
            if (File.Exists(Path)) throw new InvalidOperationException("证据文件已存在，不覆盖：" + Path);
            _header = lc is null
                ? EvidenceHeader.Build("J 路实验：" + topic, Array.Empty<string>(), Array.Empty<string>(), double.NaN, null, null, root: Repo)
                : EvidenceHeader.ForLineCase("J 路实验：" + topic, lc, root: Repo);
            L($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　出处：Pt_Optimize.Tests/R48J_ExperimentProbeTests.cs");
        }
        public void L(string s)
        {
            _sb.Append($"[{_sw.Elapsed.TotalSeconds,7:0.0} s] ").Append(s).Append('\n');
            File.WriteAllText(Path, _header + _sb.ToString(), new UTF8Encoding(false));
        }
    }

    private static string MeshOf(LineCase lc)
        => string.Join("　", typeof(LineCase).GetFields(BindingFlags.Public | BindingFlags.Instance)
                                                .Where(f => f.Name.StartsWith("Mesh", StringComparison.Ordinal))
                                                .Select(f => $"{f.Name}={f.GetValue(lc)}"));

    private static string ChecksOf(LineResult r, params string[] keys)
        => string.Join("　", keys.Select(k => r.Find(k) is { } c
               ? $"{Criteria.Plain(c.Name)} {c.Actual:0.###}（限 {c.Limit:0.###}，{(c.Undetermined ? "判不了" : c.Ok ? "过" : "不过")}，取自 {c.Where}）"
               : $"{k} 没有这一行"));

    // ════════════ J1：导航遍即终局时，终局复核的网格与求根的网格是不是同一张
    [Trait("速度", "慢")]
    [Fact]
    public void J1_终局复核网格_导航遍即终局()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        var (fine, radius) = MeshVerify.RequiredMeshFor(d, p);
        var opt = new SolverOptions { FineMm = fine, FineRadiusMm = radius, MaxRounds = 0 };
        var pr = new Probe("J1_终局复核网格", d.BuildCase(p, checkRamp: true));
        pr.L($"算例：DesignSpec.W08（「{d.Name}」），MeshVerify.RequiredMeshFor ⇒ 细网格 {fine} mm、细区半径 {radius} mm；SolverOptions.MaxRounds = 0（导航遍一轮都不跑 ⇒ 第一遍必然没走通 ⇒ lastOpt = 导航选项）");
        var nav = Solver.NavOptionsOf(opt);
        pr.L($"导航选项（Solver.NavOptionsOf）：FineMm={nav.FineMm} FineRadiusMm={nav.FineRadiusMm} ScreenCoarseMm={nav.ScreenCoarseMm}");

        var res = Solver.Solve(d, p, opt);
        pr.L($"Solve 返回：Feasible={res.Feasible} FineRefined={res.FineRefined} StopWhy=「{res.StopWhy}」 Solves={res.Solves}");
        var root = res.Design.BuildCase(p, checkRamp: true);
        Solver.ApplyCaseMesh(root, nav);
        var fin = Solver.FinishCase(res.Design, p, nav);
        pr.L("求根算例（BuildCase + Solver.ApplyCaseMesh(导航选项)）：" + MeshOf(root));
        pr.L("终局算例（Solver.FinishCase(导航选项)）　　　　　　　：" + MeshOf(fin));
        for (int j = 0; j < root.FlangeCount; j++)
            pr.L($"片{j} 网格格数：求根 {LineRunner.PlateMeshAnalytic(root, j).CellCount}　终局 {LineRunner.PlateMeshAnalytic(fin, j).CellCount}");
        pr.L($"Solve 的 res.Best.MeshCells（终局复核那次整线解片0 的格数）= {res.Best?.MeshCells}");

        // 后果：同一设计、两张网格各解一次整线（带升温），三条卡交付判据并列
        var rRoot = LineRunner.Run(root);
        pr.L($"求根网格上整线解：Ok={rRoot.Ok} Converged={rRoot.Converged} AllOk={rRoot.AllOk}　"
             + ChecksOf(rRoot, LineResult.Key.NetFlux, LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc));
        pr.L("　逐片抽热 " + string.Join("　", rRoot.Flanges.Select(f => $"{f.Name} {f.QFromTubeW:+0.000;-0.000} W")));
        if (res.Best is { } b)
        {
            pr.L($"终局网格上整线解（res.Best）：Ok={b.Ok} Converged={b.Converged} AllOk={b.AllOk}　"
                 + ChecksOf(b, LineResult.Key.NetFlux, LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc));
            pr.L("　逐片抽热 " + string.Join("　", b.Flanges.Select(f => $"{f.Name} {f.QFromTubeW:+0.000;-0.000} W")));
        }
        pr.L("完");
    }

    // ════════════ J2：图纸路径「法兰截面 J」限值是否跟设定 J 走
    [Trait("速度", "慢")]
    [Fact]
    public void J2_图纸路径截面J限值_J设8()
    {
        var pr = new Probe("J2_图纸路径截面J限值");
        // (a) 界面：图纸模式、J 设 8，页面造的算例
        var page = new LineDesignPage(new DesignInputs());
        T F<T>(string name) => (T)typeof(LineDesignPage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        F<System.Windows.Forms.RadioButton>("_src3dm").Checked = true;
        F<System.Windows.Forms.NumericUpDown>("_jDesign").Value = 8m;
        var files = F<System.Windows.Forms.TextBox[]>("_file3dm");
        foreach (var tb in files) tb.Text = "实验用不存在.3dm";
        var lcPage = (LineCase)typeof(LineDesignPage).GetMethod("BuildCase", BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!.Invoke(page, null)!;
        pr.L($"(a) 界面图纸模式、J 控件 = {F<System.Windows.Forms.NumericUpDown>("_jDesign").Value}：LineDesignPage.BuildCase() 造出的算例 FlangeFile3dm {lcPage.FlangeFile3dm.Length} 份、FlangePlates {lcPage.FlangePlates.Length} 份，"
             + $"JDesignAPerMm2 = {lcPage.JDesignAPerMm2} ⇒ 判据限值 SectionSizing.JCheckOf = {SectionSizing.JCheckOf(lcPage.JDesignAPerMm2)}");

        // (b) CloneCase 漏拷：逐字段比
        var src = new LineCase();
        foreach (var fld in typeof(LineCase).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object? v = fld.FieldType == typeof(double) ? 7.25 : fld.FieldType == typeof(int) ? 7 : fld.FieldType == typeof(bool) ? !(bool)fld.GetValue(src)!
                      : fld.FieldType == typeof(string) ? "实验值" : fld.FieldType == typeof(double[]) ? new[] { 7.25 } : null;
            if (v is not null) fld.SetValue(src, v);
        }
        var cl = FlangeAutoSizer.CloneCase(src);
        var lost = typeof(LineCase).GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(fld => !Equals(Show(fld.GetValue(src)), Show(fld.GetValue(cl))))
            .Select(fld => $"{fld.Name}（原 {Show(fld.GetValue(src))} → 副本 {Show(fld.GetValue(cl))}）").ToList();
        pr.L($"(b) FlangeAutoSizer.CloneCase 没带过去的字段（只试了 double／int／bool／string／double[] 型）{lost.Count} 个：" + string.Join("；", lost));

        // (c) 图纸路径整线解：J=8 直接解 vs 经 CloneCase（ApplyFinalMesh 细网格重解与加密复算工厂都走它）
        var p = new DesignInputs();
        var d = R47NavGridInstrumentTests.Disc56TwoSegs();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        var lc = d.BuildCase(p, checkRamp: false);
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        var g = d.Plate(1, d.DiscFloorMm(p)); g.HoleRadiusMm = holeR;
        lc.FlangePlates = Array.Empty<FlangePlate>();
        lc.FlangeFields = new[] { AnalyticSurrogate.Rasterize(g, 0.5, 2.0) };
        lc.GeomForJudge = new[] { g };
        lc.TabInsul3dmPerPlateMm = new[] { 2.8 };
        lc.JDesignAPerMm2 = 8;
        var r1 = LineRunner.Run(lc);
        var c1 = r1.Find(LineResult.Key.SectionJ);
        pr.L($"(c1) 图纸路径（内存厚度场，盘Ø56 一段），算例 J = 8 直接解：Ok={r1.Ok}　法兰截面 J 实际 {c1?.Actual:0.###} 限值 {c1?.Limit:0.###} {(c1?.Ok == true ? "过" : "不过")}");
        var viaClone = FlangeAutoSizer.ApplyFinalMesh(lc, new FlangeAutoSizer.Options { FinalMeshFineMm = lc.MeshFineMm, FinalMeshFineRadiusMm = lc.MeshFineRadiusMm });
        pr.L($"(c2) 同一算例经 FlangeAutoSizer.ApplyFinalMesh（内部 CloneCase，终局网格取同尺寸）：副本 JDesignAPerMm2 = {viaClone.JDesignAPerMm2}");
        var r2 = LineRunner.Run(viaClone);
        var c2 = r2.Find(LineResult.Key.SectionJ);
        pr.L($"(c2) 副本整线解：Ok={r2.Ok}　法兰截面 J 实际 {c2?.Actual:0.###} 限值 {c2?.Limit:0.###} {(c2?.Ok == true ? "过" : "不过")}");
        pr.L("完");
    }

    private static string Show(object? v) => v switch
    {
        null => "null",
        double[] a => "[" + string.Join(",", a) + "]",
        Array a => a.GetType().Name + "×" + a.Length,
        _ => v.ToString() ?? "",
    };

    // ════════════ J3：一片温度场不收敛时，求解器拿它做了什么决定、停机说了什么
    [Trait("速度", "慢")]
    [Fact]
    public void J3_一片温度场不收敛_求解器停机病因()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        var (_, radius) = MeshVerify.RequiredMeshFor(d, p);
        var pr = new Probe("J3_一片温度场不收敛", d.BuildCase(p, checkRamp: false));
        pr.L("实验手段：临时在 ShellThermal 外层 Picard 轮数上限加实验钩子（标 EXP-J3-TEMP，实验后删），环境变量 PT_EXP_J3_PLATE=片号 时只把那一片压到 2 轮。");
        pr.L("先在导航网格上单解一次整线，确认那一片真的判成「场没解到位」、且没熔、Ok 仍为真（否则走的是熔化那一支，不是本实验要的情形）。");
        try
        {
            Environment.SetEnvironmentVariable("PT_EXP_J3_PLATE", "1");
            var lc0 = d.Clone().Fit().BuildCase(p, checkRamp: false);
            Solver.ApplyCaseMesh(lc0, new SolverOptions { FineRadiusMm = radius });
            var r0 = LineRunner.Run(lc0);
            pr.L($"单解：Ok={r0.Ok} Converged={r0.Converged} OverMelt={r0.OverMelt} AllOk={r0.AllOk}　" + string.Join("　", r0.Flanges.Select(f => $"{f.Name} FieldsConverged={f.FieldsConverged} 峰 {f.TMaxC:0} °C 抽热 {f.QFromTubeW:+0.00;-0.00} W{(f.FieldNote.Length > 0 ? "（" + f.FieldNote + "）" : "")}")));
            foreach (var c in r0.Checks.Where(c => c.Kind != CheckKind.Reference))
                pr.L($"　判据 {Criteria.Plain(c.Name)}：{c.Actual:0.###}／{c.Limit:0.###} {(c.Undetermined ? "判不了" : c.Ok ? "过" : "不过")}");
            foreach (var key in new[] { LineResult.Key.NetFlux, LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc })
                pr.L($"　Solver.PlateSlack（{Criteria.Plain(key)}）逐片：" + string.Join("／", Enumerable.Range(0, r0.Flanges.Length).Select(j => Solver.PlateSlack(r0, key, j, lc0.ColdUnderTcMaxK, lc0.HotOverTcMaxK).ToString("+0.000;-0.000"))));

            var opt = new SolverOptions { FineMm = 0, FineRadiusMm = radius, MaxRounds = 2 };
            var prog = new Progress<string>(s => { });
            var res = Solver.Solve(d, p, opt, new ActionProgress(s => pr.L("　轨迹｜" + s)));
            pr.L($"Solve（导航遍，最多 2 轮）：Feasible={res.Feasible} HitBound={res.HitBound} Solves={res.Solves}");
            pr.L($"StopWhy =「{res.StopWhy}」");
            pr.L($"NullWhy =「{res.NullWhy}」");
            if (res.Best is { } b)
                pr.L($"终局 res.Best：Ok={b.Ok} Converged={b.Converged} AllOk={b.AllOk}　片1 FieldsConverged={(b.Flanges.Length > 1 ? b.Flanges[1].FieldsConverged : (bool?)null)}");
        }
        finally { Environment.SetEnvironmentVariable("PT_EXP_J3_PLATE", null); }
        pr.L("完");
    }

    internal sealed class ActionProgress : IProgress<string>
    {
        private readonly Action<string> _a;
        public ActionProgress(Action<string> a) => _a = a;
        public void Report(string value) { lock (this) _a(value); }
    }

    // ════════════ J4：加密复算的某一档外层耦合没收敛，会不会照样打「网格无关」
    [Trait("速度", "慢")]
    [Fact]
    public void J4_加密复算_耦合没收敛的档()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        var (h0, radius) = MeshVerify.RequiredMeshFor(d, p);
        double innerR = MeshAdapt.InnerRadiusFor(d.HoleRadiusMm, Math.Max(d.TabThickMm.Max(), d.WallMm));
        var pr = new Probe("J4_加密复算耦合没收敛", d.BuildCase(p, checkRamp: true));
        var fac = MeshVerify.AnalyticCaseFactory(d, p, radius, innerR);
        LineCase Fac(double a, double b) { var lc = fac(a, b); lc.CoupleMaxRounds = 3; return lc; }
        double hStart = 2.0;
        pr.L($"算例 W08；工厂 = MeshVerify.AnalyticCaseFactory 造完再把 CoupleMaxRounds 压到 3（外层耦合必然停在没收敛）；起始 {hStart} mm（生产起始 {h0} mm，为省时从粗档起）、半径 {radius}、内带半径 {innerR}、最多 4 档、单元上限 40000");
        var res = MeshVerify.Run(Fac, hStart, radius, innerR, maxCells: 40000, maxRounds: 4, progress: new ActionProgress(s => pr.L("　进度｜" + s)));
        pr.L($"结果：Converged={res.Converged} Undecidable={res.Undecidable} HitCellCap={res.HitCellCap} FineMm={res.FineMm}");
        pr.L($"判词：{res.Verdict}");
        if (res.Line is { } l) pr.L($"res.Line：Ok={l.Ok} Converged={l.Converged} AllOk={l.AllOk} 外层耦合剩余误差估计 {l.CoupleRemainK:0.###} K");
        foreach (var t in res.Trace) pr.L($"　档 {t.Fine:0.###} mm {t.Cells} 格：净流入 {t.N2p:0.###} 热侧 {t.N2pp:0.###} 冷侧 {t.N3:0.###}");
        pr.L("完");
    }

    // ════════════ J6：整片热稳定逐片评，裕度最小的是不是发热最大那片
    [Trait("速度", "慢")]
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void J6_整片热稳定逐片评(int which)
    {
        var p = new DesignInputs();
        // 2026-09-16 Opus 5：原写 Builtin[0]，而 Builtin[0] 就是 W08 ⇒ 09-15 的 _1 文件是 W08 的重复跑（两份数逐位相同）；改成 Builtin[1]（管壁 0.6 · 底档）。
        var d = (which == 0 ? DesignSpec.W08 : DesignSpec.Builtin[1]).Clone();
        var (_, radius) = MeshVerify.RequiredMeshFor(d, p);
        var lc = d.BuildCase(p, checkRamp: false);
        Solver.ApplyCaseMesh(lc, new SolverOptions { FineRadiusMm = radius });
        var pr = new Probe($"J6_整片热稳定逐片评_{which}", lc);
        pr.L($"算例「{d.Name}」：舌保温 {string.Join("/", d.TabInsulMm)} mm、圆盘保温逐片 {string.Join("/", d.DiscInsulMm ?? Array.Empty<double>())}、板厚 {string.Join("/", d.TabThickMm)}；导航网格、细区半径 {radius}");
        var r = LineRunner.Run(lc);
        pr.L($"整线解 Ok={r.Ok} Converged={r.Converged}");
        var pick = LineRunner.FlangeLumped(lc, r.Flanges);
        // ★ 2026-09-17，Opus 5：原来这句写死「（发热最大那片）」—— 改成逐片评之后它就成了假话
        //   （09-17 W08 实跑：生产选的是片3，而发热最大的是片0）。改成印出发热最大的是谁、再印生产自己给的挑片说明 SelectNote，
        //   探针不另写一份挑片口径。
        int hot = 0;
        for (int j = 1; j < r.Flanges.Length; j++) if (r.Flanges[j].QGenW > r.Flanges[hot].QGenW) hot = j;
        pr.L($"生产 FlangeLumped 选的片：{pick?.Index}（发热最大的是片{hot}）；挑片说明：{pick?.SelectNote}；判据表整片热稳定：{ChecksOf(r, LineResult.Key.FlangeStab)}");
        // ★ 2026-09-17 Opus 5（J 路，独立复核「必须修 2」）：升温那一行的**附注全文**照印。
        //   复核指出「升温两节点逐片结果进附注」只接在「选中片解出来」那一支，而这两份算例走的正是另一支（四片全算不出）
        //   ⇒ 那句话在实测算例上一次都没兑现。修完重跑这两档，看逐片是不是真出现在界面那一行的附注里。
        pr.L($"判据表升温期法兰−管峰值：{ChecksOf(r, LineResult.Key.RampField)}");
        pr.L($"　该行附注全文：{r.Find(LineResult.Key.RampField)?.Note}");
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            var sw = Stopwatch.StartNew();
            var o = LineRunner.FlangeLumpedAt(lc, r.Flanges, j)!;
            pr.L($"片{j} {r.Flanges[j].Name}：发热 {r.Flanges[j].QGenW:0.00} W　整片热稳定裕度 {o.Stab.Margin:0.0000}（Undetermined={o.Stab.Undetermined}）　舌保温 {o.ThermalSetup.TabInsulThickMm:0.0} mm　圆盘保温 {o.DiscInsulMm:0.0} mm"
                 + $"　升温两节点 {(o.Ramp is { } rp ? $"法兰−管峰值 {rp.MaxFlangeMinusTubeK:0.00} K" : "算不出：" + o.RampError)}　用时 {sw.Elapsed.TotalMilliseconds:0} ms");
        }
        pr.L("完");
    }

    // ════════════ J7：FlangeAutoSizer 的三个判定式遇到 NaN 怎么走
    [Trait("速度", "慢")]
    [Fact]
    public void J7_定尺寸器判定式遇NaN()
    {
        var pr = new Probe("J7_定尺寸器判定式遇NaN");
        pr.L($"Enumerable.Max(new[] {{ NaN, 1.0 }}) = {new[] { double.NaN, 1.0 }.Max()}；Max(new[] {{ 1.0, NaN }}) = {new[] { 1.0, double.NaN }.Max()}；Max(全 NaN) = {new[] { double.NaN, double.NaN }.Max()}；Math.Max(0.0, NaN) = {Math.Max(0.0, double.NaN)}");
        var v = new LineResult
        {
            Flanges = new[] { new FlangeOut { Name = "片0", QFromTubeW = 10.0 }, new FlangeOut { Name = "片1", QFromTubeW = double.NaN } },
            Segments = new[] { new SegmentOut { Name = "段0", FlangeDipK = 1.0 }, new SegmentOut { Name = "段1", FlangeDipK = double.NaN } },
        };
        double w = FlangeAutoSizer.DrawOffTargetWorstW(v, 10.0);
        pr.L($"(371 行) 全精度复核：片抽热 10.0 与 NaN、靶 10、容差 DrawTolW 缺省 {new FlangeAutoSizer.Options().DrawTolW} ⇒ DrawOffTargetWorstW = {w} ⇒ `worst < DrawTolW` = {w < new FlangeAutoSizer.Options().DrawTolW}（真 = 当达标）");
        double k = FlangeAutoSizer.DipOffTargetWorstK(v, 10.0);
        pr.L($"(1262 行) 逐级定厚复核：段增量温降 1.0 与 NaN、靶 10、容差 TolK 缺省 {new FlangeAutoSizer.Options().TolK} ⇒ DipOffTargetWorstK = {k} ⇒ `worst < TolK` = {k < new FlangeAutoSizer.Options().TolK}");
        var fAll = new FlangeOut { LevelTMaxC = new[] { double.NaN, double.NaN } };
        var fOne = new FlangeOut { LevelTMaxC = new[] { 1300.0, double.NaN } };
        double thr = new FlangeAutoSizer.Options().OverheatRaiseFromC;
        pr.L($"(1066／1094 行) 加厚试探：各级峰值全 NaN ⇒ LevelHottestC = {FlangeAutoSizer.LevelHottestC(fAll)} ⇒ `HotAt <= OverheatRaiseFromC({thr})` = {FlangeAutoSizer.LevelHottestC(fAll) <= thr}（真 = 二分当「压住了」）");
        pr.L($"　一级 1300、一级 NaN ⇒ LevelHottestC = {FlangeAutoSizer.LevelHottestC(fOne)}（NaN 那一级被跳过）");
        pr.L("(951／1145 行) 内层与熔点闸：代码读 `double e = double.IsNaN(tm) ? 0 : tm - baseT` 与 `if (!double.IsNaN(tm) && tm > hottestC)` ⇒ NaN 级当「不比管根热」、不进最热级，熔点闸 `hottestC > PtMeltC` 看不见它（读码，见 FlangeAutoSizer.cs 该段）。");
        pr.L("完");
    }

    // ════════════ J9：整面接触两种定义 —— 生产上 ClampCell 长度 = 格数但一格为真都没有，可达吗
    [Trait("速度", "慢")]
    [Fact]
    public void J9_整面接触全假可达性扫描()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone().Fit();
        var lc = d.BuildCase(p, checkRamp: false);
        var (_, radius) = MeshVerify.RequiredMeshFor(d, p);
        var pr = new Probe("J9_整面接触全假可达性");
        var g = lc.FlangePlates[0];
        pr.L($"算例 W08 片0 板件；界面压接长控件下限 3 mm（LineDesignPage._clampLen）；DesignSpec.W08.ClampLengthMm = {d.ClampLengthMm}");
        foreach (double h in new[] { 4.0, 3.0, 2.0, 1.0, 0.5, 0.25 })
            foreach (double cl in new[] { 0.5, 1.0, 1.5, 2.0, 3.0, 5.0, 40.0 })
            {
                var m = FlangeMesher.Build(g, 0, h, lc.MeshCoarseMm * h / lc.MeshFineMm, radius, cl);
                bool full = m.ClampCell.Length == m.CellCount, any = m.ClampCell.Any(b => b);
                pr.L($"h {h,5} mm　压接长 {cl,5} mm：ClampCell 长度=格数 {full}　至少一格为真 {any}　ClampFaceActive {m.ClampFaceActive}　网格配方整面接触 {m.Recipe!.ClampFullFace}　{(full && !any ? "★ 两种定义不一致" : "")}　记录：{m.ClampAnchorNote}");
            }
        pr.L("完");
    }

    // ════════════ J8：导航遍抬过头、细网格遍退不回 —— 找实例
    [Trait("速度", "慢")]
    [Fact]
    public void J8_导航遍抬过头找实例()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        // 2026-09-16 Opus 5：第一跑（2026-09-15_190647，W08 原样四片）在片 3 处被周额度中断，前三片只抬了舌保温（在回收名单里）、圆盘槽在 r=30 的盘上开不出、舌孔没变好 ⇒ W08 上找不到实例。
        //   第二跑用环境变量换算例：PT_J8_DISC = 盘半径 mm（让槽开得出）、PT_J8_SEGS = 段数（省时）。不给 = 第一跑的算例。
        string tag = "";
        if (double.TryParse(Environment.GetEnvironmentVariable("PT_J8_DISC"), out double discR) && discR > 0) { d.DiscRadiusMm = discR; tag += $"_盘R{discR:0}"; }
        if (int.TryParse(Environment.GetEnvironmentVariable("PT_J8_SEGS"), out int segs) && segs > 0 && segs < d.SetpointC.Length)
        {
            d.SetpointC = d.SetpointC.Take(segs).ToArray(); d.SegLengthMm = d.SegLengthMm.Take(segs).ToArray(); tag += $"_{segs}段";
        }
        d = d.Fit();
        var (fine, radius) = MeshVerify.RequiredMeshFor(d, p);
        var pr = new Probe("J8_导航遍抬过头找实例" + tag, d.BuildCase(p, checkRamp: false));
        pr.L($"算例 W08{(tag.Length > 0 ? "（改：" + tag.TrimStart('_') + "；盘半径 " + d.DiscRadiusMm + " mm、段数 " + d.SetpointC.Length + "）" : "")}；判决网格（第二遍）= MeshVerify.RequiredMeshFor ⇒ {fine} mm、半径 {radius} mm。先只跑导航遍（FineMm = 0），看它抬了哪些旋钮。");
        var navOpt = new SolverOptions { FineMm = 0, FineRadiusMm = radius, MaxRounds = 60 };
        var res = Solver.Solve(d, p, navOpt, new ActionProgress(s => pr.L("　导航轨迹｜" + s.Split('\n')[0])));
        var dn = res.Design;
        pr.L($"导航遍结果：Feasible={res.Feasible} StopWhy=「{res.StopWhy}」 场解 {res.Solves} 次");
        int np = dn.TabThickMm.Length;
        var knobs = new[] { Solver.Knob.Thick, Solver.Knob.Insul, Solver.Knob.Ring, Solver.Knob.RingT2, Solver.Knob.RingR1, Solver.Knob.RingR2, Solver.Knob.SlotSpan, Solver.Knob.TabHoleR, Solver.Knob.TabHoleAspect };
        var fresh = new SolverResult();
        var floor = d.Clone();
        Solver.ApplySectionFloor(floor, p, navOpt, fresh, null, _ => { });
        double Lo(Solver.Knob k, int j) => k switch
        {
            Solver.Knob.Thick => floor.TabThickMm[j], Solver.Knob.Insul => navOpt.InsLoMm, Solver.Knob.Ring or Solver.Knob.RingT2 => navOpt.RingLo,
            Solver.Knob.SlotSpan or Solver.Knob.TabHoleR => 0.0, Solver.Knob.TabHoleAspect => 1.0,
            Solver.Knob.RingR1 => dn.RingWidthMm, Solver.Knob.RingR2 => 2 * dn.RingWidthMm, _ => double.NaN,
        };
        double Val(DesignSpec s, Solver.Knob k, int j) => k switch
        {
            Solver.Knob.Thick => s.TabThickMm[j], Solver.Knob.Insul => s.TabInsulMm[j], Solver.Knob.Ring => s.RingMul[j],
            Solver.Knob.RingT2 => double.IsNaN(s.RingMul2[j]) ? s.RingMulOuter(j) : s.RingMul2[j],
            Solver.Knob.RingR1 => double.IsNaN(s.RingW1Mm[j]) ? s.RingWidthMm : s.RingW1Mm[j],
            Solver.Knob.RingR2 => double.IsNaN(s.RingW2Mm[j]) ? 2 * s.RingWidthMm : s.RingW2Mm[j],
            Solver.Knob.SlotSpan => j < s.SlotSpanDeg.Length ? s.SlotSpanDeg[j] : 0,
            Solver.Knob.TabHoleR => j < s.TabHoleRMm.Length ? s.TabHoleRMm[j] : 0,
            Solver.Knob.TabHoleAspect => j < s.TabHoleAspect.Length ? s.TabHoleAspect[j] : 1, _ => double.NaN,
        };
        var raised = new List<(Solver.Knob K, int J, double V, double Lo)>();
        for (int j = 0; j < np; j++)
            foreach (var k in knobs)
            {
                double v = Val(dn, k, j), lo = Lo(k, j);
                if (!double.IsNaN(v) && !double.IsNaN(lo) && v > lo + 1e-9) raised.Add((k, j, v, lo));
            }
        pr.L("导航遍抬过下角的旋钮：" + (raised.Count == 0 ? "没有" : string.Join("；", raised.Select(x => $"片{x.J} {x.K} {x.V:0.###}（下角 {x.Lo:0.###}）"))));
        var reclaim = new[] { Solver.Knob.Insul, Solver.Knob.Ring, Solver.Knob.RingT2 };
        var stuck = raised.Where(x => !reclaim.Contains(x.K)).ToList();
        pr.L("其中不在回收名单（舌保温／t₁／t₂）里的：" + (stuck.Count == 0 ? "没有 ⇒ 本算例上 B-1 不成立（没有抬过头又退不回的旋钮）" : string.Join("；", stuck.Select(x => $"片{x.J} {x.K}"))));
        if (stuck.Count == 0 || !res.Feasible) { pr.L("完"); return; }

        var fineOpt = new SolverOptions { FineMm = fine, FineRadiusMm = radius };
        LineResult EvalOn(DesignSpec s)
        {
            var lcx = s.BuildCase(p, checkRamp: false);
            Solver.ApplyCaseMesh(lcx, fineOpt);
            return LineRunner.Run(lcx);
        }
        string Slacks(LineResult r, LineCase lcx) => string.Join("　", new[] { LineResult.Key.NetFlux, LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc }
            .Select(key => Criteria.Plain(key) + " " + string.Join("/", Enumerable.Range(0, np).Select(j => Solver.PlateSlack(r, key, j, lcx.ColdUnderTcMaxK, lcx.HotOverTcMaxK).ToString("+0.00;-0.00")))));
        var lc0 = dn.BuildCase(p, checkRamp: false);
        var r0 = EvalOn(dn);
        pr.L($"导航解放到判决网格（{fine} mm）：Ok={r0.Ok} Converged={r0.Converged} AllOk={r0.AllOk} 合计 {r0.TotalMassG:0.0} g　逐片裕度 {Slacks(r0, lc0)}");
        foreach (var x in stuck)
        {
            var dx = dn.Clone();
            Solver.SetKnob(dx, x.K, x.J, x.Lo, p, res);
            var rx = EvalOn(dx);
            pr.L($"只把 片{x.J} {x.K} {x.V:0.###} → 下角 {x.Lo:0.###}：Ok={rx.Ok} Converged={rx.Converged} AllOk={rx.AllOk} 合计 {rx.TotalMassG:0.0} g（{rx.TotalMassG - r0.TotalMassG:+0.0;-0.0} g）　逐片裕度 {Slacks(rx, lc0)}");
        }
        pr.L("完");
    }

    // ════════════ J6 乙：找一个一段（两片）的算例，发热最大的片不是整片热稳定裕度最小的片（给快门用，免得快门跑四片整线）
    [Trait("速度", "慢")]
    [Fact]
    public void J6乙_一段两片找发热最大不等于裕度最小()
    {
        var p = new DesignInputs();
        var pr = new Probe("J6乙_一段两片找非空转算例");
        foreach (var ins in new[] { new[] { 0.3, 0.3 }, new[] { 0.3, 8.0 }, new[] { 8.0, 0.3 }, new[] { 0.3, 15.0 } })
        {
            var d = DesignSpec.W08.Clone();
            d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
            d = d.Fit();
            d.TabInsulMm = (double[])ins.Clone();
            var lc = d.BuildCase(p, checkRamp: false);
            var sw = Stopwatch.StartNew();
            var r = LineRunner.Run(lc);
            string rows = string.Join("　", Enumerable.Range(0, r.Flanges.Length).Select(j =>
            {
                var o = LineRunner.FlangeLumpedAt(lc, r.Flanges, j)!;
                return $"{r.Flanges[j].Name} 发热 {r.Flanges[j].QGenW:0.00} W 裕度 {o.Stab.Margin:0.0000}";
            }));
            pr.L($"舌保温 {string.Join("/", ins)}：Ok={r.Ok} {sw.Elapsed.TotalSeconds:0.0} s　{rows}");
        }
        pr.L("完");
    }

    // ════════════ J10 乙：保温搜索内层网格格数随不随板厚变（决定行为门能不能用传入设计直接造参照网格）
    [Trait("速度", "慢")]
    [Fact]
    public void J10乙_保温搜索内层网格_截日志()
    {
        var p = new DesignInputs();
        var d0 = DesignSpec.W08.Clone().Fit();
        var pr = new Probe("J10乙_保温搜索内层网格截日志");
        double radius = MeshVerify.RequiredMeshFor(d0, p).RadiusMm;
        foreach (var dt in new[] { 0.0, 0.5, 1.5 })
        {
            var d = d0.Clone();
            d.TabThickMm = d.TabThickMm.Select(v => v + dt).ToArray();
            var lc = d.BuildCase(p, checkRamp: false);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 1.0, FineRadiusMm = radius });
            var lc0 = d.BuildCase(p, checkRamp: false);
            Solver.ApplyCaseMesh(lc0, new SolverOptions { FineMm = 1.0, FineRadiusMm = 0 });
            pr.L($"板厚 +{dt}：半径 {radius} 逐片格数 {string.Join("/", Enumerable.Range(0, lc.FlangeCount).Select(j => LineRunner.PlateMeshAnalytic(lc, j).CellCount))}　半径 0（不传）{string.Join("/", Enumerable.Range(0, lc0.FlangeCount).Select(j => LineRunner.PlateMeshAnalytic(lc0, j).CellCount))}");
        }
        string? got = null;
        var stop = new InvalidOperationException("截到内层网格那一行，停");
        try
        {
            InsulationSearch.Run(DesignSpec.W08, p, new InsulationSearch.Options
            {
                TubeLayers = new[] { 40 }, InnerMeshMm = 1.0, DiscLayerMax = 1, TabLayerMax = 1,
                Log = line => { if (line.Contains("内层网格 h=", StringComparison.Ordinal)) { got = line; throw stop; } },
            });
        }
        catch (Exception ex) when (ReferenceEquals(ex, stop) || ReferenceEquals(ex.InnerException, stop) || ex is AggregateException) { }
        pr.L("截到：" + (got ?? "没截到"));
        pr.L("完");
    }
}
