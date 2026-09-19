using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 重解前的四个探针（2026-09-14，Opus 5 写；规格按常驻物理、数值把关人两轮审查意见定）。
/// ⛔ 2026-09-14 14:19 更正（Opus 5；物理把关人第五轮查出，deliverable/R48_接头电流核对_2026-09-14.txt 核实）：
///   本探针的片电流 1214 / 2102 / 2102 / 1214 A 是**升温设计电流**，不是整线稳态接头电流（1213 / 1984 / 1817 / 1022 A，导航网格整线）；
///   焦耳热比约 1.00 / 1.12 / 1.34 / 1.41。管根也不是整线值（整线 1172.63 / 1151.19 / 1093.09 / 1056.80 °C）。
///   ⇒ 同一工作点上的配对比较结论可用，片1～片3 的绝对值（抽热、敏感度、误差量级）**不代表设计工作点**，不许拿去做规划或判据。
///
/// 被测设计一律是管壁 0.8、09-13 求解器落点（板厚 0.73/1.26/1.26/0.73，舌保温 4.60/2.10/2.90/7.50），
/// 且**已是保温按半径划之后的模型**。每点算完立刻落盘。
/// 判读规则**写在跑之前**（物理把关人：「结果出来再定做不做，容易变成看了结果再找理由」）。
/// </summary>
[Trait("速度", "慢")]
public class R48PreResolveProbesTests
{
    private readonly ITestOutputHelper _out;
    public R48PreResolveProbesTests(ITestOutputHelper o) { _out = o; }

    private static DesignSpec Landing()
    {
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        d.SizeTongues(new DesignInputs());
        return d;
    }

    private Action<string> Logger(string file, StringBuilder sb) => s =>
    {
        _out.WriteLine(s); sb.AppendLine(s);
        try { File.WriteAllText(Path.Combine(Root(), "deliverable", file), sb.ToString(), new UTF8Encoding(false)); } catch { }
    };

    private static double V(LineResult r, string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
    private static string W(LineResult r, string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Where ?? "";

    private static LineResult RunLine(LineCase lc, Action<string> say, string tag)
    {
        var sw = Stopwatch.StartNew(); double last = 0;
        var tick = new Progress<string>(m =>
        {
            if (sw.Elapsed.TotalSeconds - last < 90) return;
            last = sw.Elapsed.TotalSeconds;
            say($"     · {tag}　[已跑 {sw.Elapsed.TotalSeconds:0} s] {m}");
        });
        return LineRunner.Run(lc, tick, default);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 探针一（数值把关人 E10，第二轮规格）：**细区半径是不是未受控变量？**
    ///
    /// 已知整线耦合、同一 h = 2.0、只改半径 50→59：管孔净流入 +3.267 → −6.533（差 9.8 W、换号）。
    /// 规格：
    ///   · **单片、管侧固定**（与收敛阶实验同一设置）⇒ 没有外层耦合噪声。单片复现不出这个量级，
    ///     就说明那 9.8 W 来自耦合噪声或换片，不是网格。
    ///   · 加一列**均匀网格**（粗区 = 中带，相当于半径取无穷大）作参照，直接量 d(R) = |抽热(R) − 抽热(均匀)|。
    ///     这是网格族在半径→∞ 的极限，不是另起的第三个变量；不必凑单元数。
    ///   · h 取 1.0 与 0.5（2.0 不在渐近区，连舌根圆角与环宽都画不出来，只作复现诊断行、不进判读）。
    ///   · 逐片跑；另打物理把关人要的机理列，分开两个候选：
    ///       ① 舌片区的离散误差经整片能量守恒传到管孔 ⇒ 舌片区发热／舌片区散热／夹持流出随半径变得多；
    ///       ② 孔边格子本身变了 ⇒ 孔边面数／Σ(厚×边长÷形心到面距离)／平均形心到面距离随半径变得多。
    /// 判读（写在跑之前）：净流入判据取自的那一片（共用2 = HC2|HC3，片2）上
    ///   d(R=59, h=0.5) ≤ 0.45 W（净流入离限值距离 1.35 W 的 1/3，与 StableFactor 同一口径）且 d(0.5) &lt; d(1.0)
    ///   ⇒ 半径影响随加密收缩、且小到判不翻结论 ⇒ 半径可取固定输入 + 热点后验门；否则重解先别跑。
    /// 管侧条件取自 `R48_圆盘区判据覆盖_2026-09-13.txt`（落点、导航网格）：管根 1141.8/1107.8/1057.8/1038.0 °C；
    /// 片电流取设计电流 1214/2102/2102/1214 A（`R48_正式重解_管壁08_2026-09-13.txt` 设计电流行）。
    /// </summary>
    [Fact]
    public void 探针一_细区半径_单片管侧固定_均匀网格参照()
    {
        var sb = new StringBuilder(); var say = Logger("R48_探针一_细区半径_2026-09-14.txt", sb);
        say($"R48 探针一：细区半径（单片、管侧固定、均匀网格参照）（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        say("管壁 0.8 落点，保温按半径。R=∞ 即均匀网格（粗区 = 中带）。d = |抽热(R) − 抽热(∞)|。");
        var d = Landing();
        var p = new DesignInputs();
        var lc = d.BuildCase(p, checkRamp: false);
        double[] iA = { 1214, 2102, 2102, 1214 };
        double[] tRoot = { 1141.8, 1107.8, 1057.8, 1038.0 };
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        double coarseRatio = lc.MeshCoarseMm / lc.MeshFineMm;   // 自相似配方的细粗比
        var q = new Dictionary<(int j, double h, double R), double>();

        foreach (double h in new[] { 2.0, 1.0, 0.5 })
        {
            say($"── h = {h:0.0} mm{(h > 1.5 ? "（诊断行：只看单片能不能复现整线那 9.8 W，不进判读）" : "")}");
            say($"   片  半径     单元   抽热W    舌区发热W  舌区散热W  夹持流出W   孔边面数  Σt·L/d     平均d");
            for (int j = 0; j < 4; j++)
            {
                var g = d.Plate(j, d.DiscFloorMm(p));
                g.HoleRadiusMm = holeR;
                var (xa, za) = FlangeMesher.AnchorsOf(g);
                var tf = FlangeMesher.Rasterize(g, Math.Max(h / 4, 0.02));
                double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
                var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet;
                if (j < lc.ClampTempC.Length) p2.BusbarClampTempC = lc.ClampTempC[j];
                foreach (double R in new[] { 50.0, 59.0, 90.0, double.PositiveInfinity })
                {
                    bool uni = double.IsPositiveInfinity(R);
                    var m = FlangeMesher.BuildFromField(tf, holeR, 0, h, uni ? h : coarseRatio * h, uni ? 1e6 : R,
                                                        lc.Base.BusbarClampLengthMm, h, 0, g.TwoTabs, xa, za, 1.5 * h);
                    var sc = ShellCurrent.SolveFor(lc, m, iA[j], Materials.PtResistivity(tSet) * 1e3, tSet);
                    var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, tRoot[j], g.InsulBoundaryXResolved, g.TwoTabs,
                                                tabBoundaryX: g.Tangent().X, tabInsulThickMm: d.TabInsulMm[j],
                                                discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: g.InsulDiscRadiusMm);
                    q[(j, h, R)] = th.QFromTubeW;
                    var hole = m.Faces.Where(f => f.Tag == ShellMesh.TagHole).ToArray();
                    double sumG = hole.Sum(f => m.Thickness[f.A] * f.Length / Math.Max(1e-9, f.DistAB));
                    double avgD = hole.Length > 0 ? hole.Average(f => f.DistAB) : double.NaN;
                    say($"   {j}  {(uni ? "  ∞" : R.ToString("0")),4}  {m.CellCount,7}  {th.QFromTubeW,7:+0.000;-0.000}  {th.QGenTabW,9:0.00}  {th.QLossTabW,9:0.00}"
                      + $"  {th.QToClampW,9:0.00}  {hole.Length,8}  {sumG,8:0.0}  {avgD,8:0.0000}{(th.Converged ? "" : "　⚠ 热场未收敛")}");
                }
                double dq = Math.Abs(q[(j, h, 59.0)] - q[(j, h, double.PositiveInfinity)]);
                say($"   片{j} d(59) = {dq:0.000} W　|抽热(50) − 抽热(59)| = {Math.Abs(q[(j, h, 50.0)] - q[(j, h, 59.0)]):0.000} W");
            }
        }

        double d05 = Math.Abs(q[(2, 0.5, 59.0)] - q[(2, 0.5, double.PositiveInfinity)]);
        double d10 = Math.Abs(q[(2, 1.0, 59.0)] - q[(2, 1.0, double.PositiveInfinity)]);
        double rep2 = Math.Abs(q[(2, 2.0, 50.0)] - q[(2, 2.0, 59.0)]);
        say("");
        say($"★ 复现诊断（h=2.0，片2）：单片 |抽热(50) − 抽热(59)| = {rep2:0.000} W；整线实测 9.8 W。"
          + (rep2 < 2.0 ? " ⇒ 单片复现不出这个量级：那 9.8 W 主要来自外层耦合噪声或换片，不是网格本身。"
                        : " ⇒ 单片也复现得出：网格本身就有这个量级的半径效应。"));
        say($"★ 判读（片2）：d(59, h=0.5) = {d05:0.000} W（门槛 0.45）　d(59, h=1.0) = {d10:0.000} W");
        say(d05 <= 0.45 && d05 < d10
            ? "   ⇒ 半径影响随加密收缩且小于门槛 ⇒ 半径可取固定输入 + 热点后验门，重解可以跑。"
            : "   ⇒ **不满足** ⇒ 半径是未受控变量，加密阶梯的前提要重写，重解先别跑。");
        Assert.True(q.Count == 3 * 4 * 4, "单片算例没有全部解出来，别拿它下结论");
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 探针二（物理把关人 (b)，第二轮规格）：**共用片双扣开关对照。**
    ///
    /// 判读（写在跑之前）：
    ///   · **正对照必须先成立**，否则「端片没变」可能只是开关没接上：
    ///     「管↔法兰热收支差」关着时非零、开着时 |差| 显著变小；片1、片2 的抽热变化 &gt; 片0、片3 的。
    ///   · 端片不受影响：|Δ管根温度| ≤ 两次运行的外层耦合剩余误差之和（配对差里离散误差基本抵消，剩下的是耦合停机噪声）。
    ///   · 端片三条判据都要看：管根温度、圆盘区−管温、第一段 A 端增量温降。
    /// 网格：复核第 2 档（0.5 mm，整张一起缩，细区半径 59）。
    /// </summary>
    [Fact]
    public void 探针二_共用片双扣开关对照()
    {
        var sb = new StringBuilder(); var say = Logger("R48_探针二_共用片双扣对照_2026-09-14.txt", sb);
        say($"R48 探针二：共用片双扣开关对照（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　管壁 0.8 落点，保温按半径，0.5 mm");
        var (_, radius) = MeshVerify.RequiredMeshFor(Landing());
        var res = new Dictionary<bool, LineResult>();
        foreach (bool split in new[] { false, true })
        {
            var p = new DesignInputs { SplitSharedFlangeDraw = split };
            var lc = Landing().BuildCase(p, checkRamp: false);
            MeshAdapt.RefineWholeMesh(lc, 0.5, radius);
            var r = RunLine(lc, say, split ? "开" : "关");
            if (!r.Ok) { say($"开关={split}　解不出来：{r.Message}"); continue; }
            res[split] = r;
            say($"开关={(split ? "开（各半）" : "关（双扣）")}　{r.MeshCells} 单元　耦合剩余 {r.CoupleRemainK:0.00} K　"
              + $"管↔法兰热收支差 {V(r, LineResult.Key.HeatBalance):+0.00;-0.00} W");
            for (int j = 0; j < r.Flanges.Length; j++)
            {
                var f = r.Flanges[j];
                say($"   片{j}　抽热 {f.QFromTubeW,8:+0.000;-0.000} W　管根 {f.TRootC,8:0.00} °C　圆盘区−管温 {f.TDiscMaxC - f.TRootC,7:+0.000;-0.000} K");
            }
            say($"   第一段 A 端增量温降 {r.Segments[0].FlangeDipAK:+0.000;-0.000} K　末段 B 端 {r.Segments[^1].FlangeDipBK:+0.000;-0.000} K");
        }
        Assert.True(res.ContainsKey(false) && res.ContainsKey(true), "两次都得解出来");
        var off = res[false]; var on = res[true];
        double noise = off.CoupleRemainK + on.CoupleRemainK;
        double hbOff = Math.Abs(V(off, LineResult.Key.HeatBalance)), hbOn = Math.Abs(V(on, LineResult.Key.HeatBalance));
        double dqInner = Math.Max(Math.Abs(on.Flanges[1].QFromTubeW - off.Flanges[1].QFromTubeW), Math.Abs(on.Flanges[2].QFromTubeW - off.Flanges[2].QFromTubeW));
        double dqEnd = Math.Max(Math.Abs(on.Flanges[0].QFromTubeW - off.Flanges[0].QFromTubeW), Math.Abs(on.Flanges[3].QFromTubeW - off.Flanges[3].QFromTubeW));
        bool control = hbOff > 1e-3 && hbOn < 0.5 * hbOff && dqInner > dqEnd;
        say("");
        say($"★ 正对照：热收支差 关 {hbOff:0.00} → 开 {hbOn:0.00} W；共用片抽热最大变化 {dqInner:0.000} W vs 端片 {dqEnd:0.000} W ⇒ "
          + (control ? "开关确实生效" : "**正对照不成立 —— 下面的端片结论不可引用（可能开关没接上）**"));
        double dt0 = Math.Abs(on.Flanges[0].TRootC - off.Flanges[0].TRootC), dt3 = Math.Abs(on.Flanges[3].TRootC - off.Flanges[3].TRootC);
        say($"★ 端片管根变化：片0 {dt0:0.000} K　片3 {dt3:0.000} K　耦合噪声上限 {noise:0.00} K ⇒ "
          + (Math.Max(dt0, dt3) <= noise ? "与 0 分不开，「端片不受影响」成立" : "超过噪声上限，「端片不受影响」**不成立**"));
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 探针三（物理把关人，第二轮规格）：**保温按半径划之后，片0 的圆盘区最高温谁说了算？**
    ///
    /// · 杠杆一：片0 舌保温 1.0／4.6／8.0（现在只包 r &gt; 盘半径的舌片，碰不到任何圆盘格子），其余三片钉在落点。
    ///   峰所在格子若每行都包法兰保温，盘峰的变化**只能**来自舌片回灌；回灌还要求舌片区峰随舌保温一起升。
    /// · 杠杆二：法兰保温 1.0／2.5／5.0（全线同值，改不了单片）。不取 0：厚 0 时外表面发射率从 0.45 变成铂的 0.18，是另一个物理。
    ///   ⛔ 2026-09-14 下午更正（Opus 5）：**杠杆二空转**。它改的是 DesignInputs.FlangeInsulThickMm，而整线算例由 DesignSpec.BuildCase 造，
    ///     圆盘保温取 DesignSpec.FlangeInsulMm = 20，把它覆盖了（三行数据逐位相同为证）。本探针所有行的圆盘保温实际都是 **20 mm**，
    ///     文件里「法兰保温 2.5」「峰格保温=2.5/1.0/5.0 mm」都是按 DesignInputs 打的，**不是算例实际值**。
    ///     圆盘保温真正的杠杆见 R48DiscInsulLeverTests（改 DesignSpec.FlangeInsulMm，并打印算例实际值）。源码已改为打印算例实际值。
    ///   全线一起变会经管根拖动片0 ⇒ **盘峰温度与管根温度分两列打**，不只打差值。
    /// · 每行打峰位 r/x/z、当地 J、厚度、峰所在格子包的保温厚度，以及舌片区峰值与峰位 —— 峰一挪位，归因就不成立。
    /// · 净流入与增量温降在 0.5 mm 上「还没算准」，只读同一网格上的配对差；圆盘区最高温可读绝对值。
    /// 接缝 ±3 mm 敏感度要不要做（判读写在跑之前）：片0 舌保温 1.0→8.0 时圆盘区−管温的总变化量
    ///   **小于**落点处它离 5 K 的距离 ⇒ 不做（接缝挪动影响不会大于整条舌片从 1 包到 8 mm）；否则必须做。
    /// </summary>
    [Fact]
    public void 探针三_片0保温两根杠杆()
    {
        var sb = new StringBuilder(); var say = Logger("R48_探针三_片0保温两根杠杆_2026-09-14.txt", sb);
        say($"R48 探针三：片0 保温两根杠杆（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　管壁 0.8 落点，保温按半径，0.5 mm，细区半径 59");
        var (_, radius) = MeshVerify.RequiredMeshFor(Landing());

        string Row(LineResult r, DesignSpec d, DesignInputs p, double caseDiscInsul)
        {
            var f = r.Flanges[0];
            var g = d.Plate(0, d.DiscFloorMm(p));
            // 2026-09-14 更正（Opus 5）：原取 p.FlangeInsulThickMm（DesignInputs 默认 2.5），不是算例实际值 ⇒ 改取调用方传入的 lc.Base.FlangeInsulThickMm
            double peakIns = g.UnderDiscInsulation(f.DiscMaxXMm, f.DiscMaxZMm) ? caseDiscInsul : d.TabInsulMm[0];
            return $"盘峰 {f.TDiscMaxC:0.00} °C　管根 {f.TRootC:0.00} °C　差 {f.TDiscMaxC - f.TRootC:+0.000;-0.000} K"
                 + $"　峰位 r={f.DiscMaxRMm:0.0} x={f.DiscMaxXMm:+0.0;-0.0} z={f.DiscMaxZMm:+0.0;-0.0} J={f.DiscMaxJAPerMm2:0.00} 厚={f.DiscMaxThickMm:0.00} 峰格保温={peakIns:0.0} mm"
                 + $"　舌区峰 {f.TTabMaxC:0.00} °C（r={f.TabMaxRMm:0.0} x={f.TabMaxXMm:+0.0;-0.0}）"
                 + $"　｜净流入 {V(r, LineResult.Key.NetFlux):+0.000;-0.000}（{W(r, LineResult.Key.NetFlux)}）增量温降 {V(r, LineResult.Key.FlangeDip):0.000}（{W(r, LineResult.Key.FlangeDip)}）"
                 + $"　耦合剩余 {r.CoupleRemainK:0.00} K";
        }

        var disc0 = new Dictionary<double, double>();
        say("── 杠杆一：片0 舌保温（只包 r > 盘半径），其余三片钉在落点，圆盘保温取算例实际值（见每行「峰格保温」）");
        foreach (double ins in new[] { 1.0, 4.6, 8.0 })
        {
            var p = new DesignInputs();
            var d = Landing(); d.TabInsulMm[0] = ins; d.SizeTongues(p);
            var lc = d.BuildCase(p, checkRamp: false);
            MeshAdapt.RefineWholeMesh(lc, 0.5, radius);
            var r = RunLine(lc, say, $"舌保温 {ins:0.0}");
            if (!r.Ok) { say($"片0舌保温 {ins:0.0}　解不出来：{r.Message}"); continue; }
            disc0[ins] = r.Flanges[0].TDiscMaxC - r.Flanges[0].TRootC;
            say($"片0舌保温 {ins:0.0} mm　{Row(r, d, p, lc.Base.FlangeInsulThickMm)}");
        }

        say("── 杠杆二：⛔ 空转（改的是 DesignInputs，被 DesignSpec.FlangeInsulMm 覆盖），留作记录；每行打印算例实际圆盘保温");
        foreach (double fi in new[] { 1.0, 2.5, 5.0 })
        {
            var p = new DesignInputs { FlangeInsulThickMm = fi };
            var d = Landing(); d.SizeTongues(p);
            var lc = d.BuildCase(p, checkRamp: false);
            MeshAdapt.RefineWholeMesh(lc, 0.5, radius);
            var r = RunLine(lc, say, $"法兰保温 {fi:0.0}");
            if (!r.Ok) { say($"法兰保温 {fi:0.0}　解不出来：{r.Message}"); continue; }
            say($"法兰保温（设定 DesignInputs）{fi:0.0} mm，算例实际 {lc.Base.FlangeInsulThickMm:0.0} mm　{Row(r, d, p, lc.Base.FlangeInsulThickMm)}");
        }

        if (disc0.ContainsKey(1.0) && disc0.ContainsKey(8.0) && disc0.ContainsKey(4.6))
        {
            double total = Math.Abs(disc0[8.0] - disc0[1.0]);
            double dist = Math.Abs(5.0 - disc0[4.6]);
            say("");
            say($"★ 接缝判读：片0 舌保温 1.0→8.0 圆盘区−管温总变化 {total:0.000} K；落点处离 5 K 的距离 {dist:0.000} K ⇒ "
              + (total < dist ? "接缝挪 ±3 mm 的敏感度**不必做**" : "接缝挪 ±3 mm 的敏感度**必须做**"));
        }
        Assert.True(disc0.Count >= 2, "探针空转了，别拿它下结论");
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 探针四（数值把关人，第三轮）：**外层耦合停机噪声到底多大 —— 实测，不靠「放大 25 倍」那个估计。**
    ///
    /// 同一设计、同一张网格（0.5 mm），外层耦合容差分别取 1.0 K（现行）与 0.05 K（= 增量温降噪声底 0.1 K 的一半），
    /// 两次的判据差就是耦合停机噪声的实测值。
    /// 判读（写在跑之前）：|增量温降(1.0) − 增量温降(0.05)| ≥ 0.1 K ⇒ 现行耦合容差下，增量温降在档间「在摆」
    ///   与耦合噪声分不开 ⇒ 加密复核算例的耦合容差须收紧到 0.05 K；否则现行容差够用。
    /// ⚠ 数值把关人提醒：放大系数 25 已被实测推翻过（差 134 倍），量不到比值时剩余误差估计可能偏小 5 倍 —— 所以要实测。
    /// </summary>
    [Fact]
    public void 探针四_外层耦合停机噪声实测()
    {
        var sb = new StringBuilder(); var say = Logger("R48_探针四_耦合停机噪声_2026-09-14.txt", sb);
        say($"R48 探针四：外层耦合停机噪声实测（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　管壁 0.8 落点，保温按半径，0.5 mm，细区半径 59");
        var (_, radius) = MeshVerify.RequiredMeshFor(Landing());
        var res = new Dictionary<double, LineResult>();
        foreach (double tol in new[] { 1.0, 0.05 })
        {
            var p = new DesignInputs();
            var lc = Landing().BuildCase(p, checkRamp: false);
            MeshAdapt.RefineWholeMesh(lc, 0.5, radius);
            lc.CoupleTolK = tol;
            if (tol < 0.5) lc.CoupleMaxRounds = 4000;
            var sw = Stopwatch.StartNew();
            var r = RunLine(lc, say, $"耦合容差 {tol:0.00}");
            if (!r.Ok) { say($"耦合容差 {tol:0.00}　解不出来：{r.Message}"); continue; }
            res[tol] = r;
            say($"耦合容差 {tol:0.00} K　{sw.Elapsed.TotalMinutes:0.0} 分　收敛 {r.Converged}　耦合剩余估计 {r.CoupleRemainK:0.000} K"
              + $"　净流入 {V(r, LineResult.Key.NetFlux):+0.0000;-0.0000}　圆盘区 {V(r, LineResult.Key.DiscTemp):0.0000}　增量温降 {V(r, LineResult.Key.FlangeDip):0.0000}"
              + $"　逐片抽热 {string.Join("/", r.Flanges.Select(f => f.QFromTubeW.ToString("+0.000;-0.000")))}");
        }
        Assert.True(res.ContainsKey(1.0) && res.ContainsKey(0.05), "两次都得解出来");
        double dDip = Math.Abs(V(res[1.0], LineResult.Key.FlangeDip) - V(res[0.05], LineResult.Key.FlangeDip));
        double dFlux = Math.Abs(V(res[1.0], LineResult.Key.NetFlux) - V(res[0.05], LineResult.Key.NetFlux));
        double dDisc = Math.Abs(V(res[1.0], LineResult.Key.DiscTemp) - V(res[0.05], LineResult.Key.DiscTemp));
        say("");
        say($"★ 实测耦合停机噪声：增量温降 {dDip:0.0000} K　净流入 {dFlux:0.0000} W　圆盘区 {dDisc:0.0000} K"
          + $"（现行容差下的剩余估计 {res[1.0].CoupleRemainK:0.000} K）");
        say(dDip >= 0.1
            ? "   ⇒ 增量温降的耦合噪声 ≥ 噪声底 0.1 K ⇒ 加密复核算例的耦合容差**必须收紧到 0.05 K**。"
            : "   ⇒ 增量温降的耦合噪声 < 噪声底 0.1 K ⇒ 现行耦合容差够用。");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
