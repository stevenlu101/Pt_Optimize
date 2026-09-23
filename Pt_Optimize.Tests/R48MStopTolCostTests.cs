using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 M 路慢门：**停机容差改成绝对目标 + 放大改 max(闭式×1.1, 实测雅可比) 之后的成本与四行对拍** —— 2026-09-18，Fable 5.1
//
//  (c) 成本：细网格整线一次（生产默认，W08 那份复原设计）≤ §0.-10 ④ 那一跑的 1.5 倍（146 s ⇒ 219 s；跑前写死，不事后挪）。
//      ⚠ 这是墙钟门：同一台机器上别的工作树在跑（2026-09-18 r48_N 网格修复）会把它撞红 —— 撞红了照报，并把轮数与每轮秒数一起印出来供判读。
//  (d) 四行细网格对拍（10/20 mm × 细/导航；参照 = N 路门 f 的两份文件 = 旧停机口径、新网格，2026-09-23 换的，变因见 R48MCostKit.Refs）：
//      最热铂高出热偶读数／管根低于热偶读数 的位移 ≤ 0.05 K（本就收敛）；管孔净流入（瓦）只报不判；超了查因不挪门槛。
// ════════════════════════════════════════════════════════════════════════════

internal static class R48MCostKit
{
    /// <summary>§0.-10 ④：细网格（1.000 mm／半径 59.0）生产默认一次整线 146 s（deliverable/R48_L_耦合容差收紧_细网格_本次开跑于2026-09-17_224252.txt，④ 那一行）。</summary>
    internal const double RefFineSec = 146.0;
    internal const double CostFactorCap = 1.5;
    internal const double ShiftCapK = 0.05;

    internal sealed class Ref
    {
        public double DiscMm; public bool Fine;
        public double Hot, Cold, Flux; public string Source = "";
    }

    /// <summary>
    /// 四行参照。**每行的数指回它的文件。**
    ///
    /// ★ 2026-09-23（云端会话，Fable 5.1，改门写明变因）：参照从 09-17／09-18 那几跑**换成 N 路门 f 的两份文件**。
    ///   变因 = N 路网格生成根因修复并入合并树（HANDOVER §0.-15N／§0.-17）：解析板精确积分、面长按材料裁剪、管孔边界圆弧、碎格并入，
    ///   同一设计同一档网格上管根移动 0.7～1.5 K（合并树实跑 2026-09-23 01:43：四行对旧参照 +0.476／−0.901、+0.362／−1.532、+0.303／−0.745、−0.115／−1.409 K），
    ///   而本门守的是「**只换停机口径**判据值位移 ≤ 0.05 K」，参照必须是「旧停机口径、同一张（新）网格」的数 ——
    ///   N 路门 f 正是这个口径（N 树 = §0.-10 的停机口径 + 旧段解地板 + 新网格），两条路的设计、DesignInputs 与网格调用逐项相同
    ///   （R48LW08NavDesign.Build ＋ 圆盘保温 10／20 逐片同设；Solver.ApplyCaseMesh；LineRunner.Run）。
    ///   细网格两行取 09-19 那跑（含 09-19 的楔形碎格 J 截断，ShellCurrent.SliverKappaMin）；导航两行 N 只有 09-18 那跑（碎格截断之前），
    ///   细网格上截断前后差 0.007／0.000 K，导航上未量 —— 这 0.05 K 的预算里含着这一项。
    ///   导航网格随之改成与 N 门 f 相同的口径（FineMm = 0 只统一细区半径 = MeshVerify.RequiredMeshFor，2.0／59.0），见 <see cref="Mesh"/>。
    ///   旧参照（105752／224252 那几跑，旧网格）留在 git 历史里；它们对合并树不再是同一张网格。
    ///
    /// ★ 2026-09-23（云端会话，Fable 5.1，**再换参照，变因 F6**，HANDOVER §0.-19）：F6 把孔边电流场改为孔面上定电位（+ 弧面法向距、孔标签只认弧面、
    ///   热稳定锚点在孔圆），有意改了同一张网格上的场 —— 合并树门 f 实跑（deliverable/网格修复_门f_现役数_W08_本次开跑于2026-09-23_065617.txt）四行对上面 N 门 f 参照
    ///   的位移 +0.543／−0.446、+1.585／−1.102、+0.541／−0.330、+1.713／−0.821 K（最热铂／管根），本门必红；而这些位移是 F6 的，不是停机口径的。
    ///   参照必须仍是「旧停机口径、同一张网格、**含 F6**」的数。这样的树不存在，所以造了一棵：N 快照 7b7a71f（§0.-10 停机口径 CoupleTolK 0.1 按裕度收紧、旧段解地板、新网格）
    ///   ＋ F6 的 Core 四档（ShellMesh／ShellCurrent／ShellThermal／QuadMesher，补丁逐字相同；LineRunner 只有 3 行注释）—— 这四档在 N 与 F6 基准 ae8d110 上逐位相同，
    ///   补丁干净套上；不含物性接线（接线对纯铂逐位中性，§0.-19 己）。在它上面跑 N 门 f：
    ///   deliverable/网格修复_门f_现役数_W08_N树加F6参照_本次开跑于2026-09-23_074438.txt（W06 同批）。旧值 → 新值：
    ///   盘10 判决 −8.780／18.109／5.314 → −8.231／17.673／5.111；盘10 导航 −9.091／18.376／5.449 → −7.507／17.275／4.933；
    ///   盘20 判决 4.878／3.045／0.035 → 5.407／2.722／−0.102；盘20 导航 4.370／3.255／0.126 → 6.081／2.445／−0.265。
    ///   合并树门 f（新停机口径）对这组新参照的差：−0.006／−0.010、+0.001／−0.001、+0.012／−0.007、+0.002／−0.011 K —— 都在 0.05 K 内，
    ///   即「只换停机口径」的位移在 F6 之后仍很小；本门自己的实跑见 HANDOVER §0.-19 庚。阈值 0.05 K 没动。
    /// </summary>
    internal static readonly Ref[] Refs =
    {
        new() { DiscMm = 10, Fine = true,  Hot = -8.231, Cold = 17.673, Flux = 5.111, Source = "deliverable/网格修复_门f_现役数_W08_N树加F6参照_本次开跑于2026-09-23_074438.txt 行「盘10　判决 1.000／59.0　4164 格」（N 快照 7b7a71f ＋ F6 Core 四档：§0.-10 停机口径、旧段解地板、新网格、孔面上定电位）" },
        new() { DiscMm = 10, Fine = false, Hot = -7.507, Cold = 17.275, Flux = 4.933, Source = "deliverable/网格修复_门f_现役数_W08_N树加F6参照_本次开跑于2026-09-23_074438.txt 行「盘10　导航 2.000／59.0　1116 格」（同上）" },
        new() { DiscMm = 20, Fine = true,  Hot = 5.407,  Cold = 2.722,  Flux = -0.102, Source = "deliverable/网格修复_门f_现役数_W08_N树加F6参照_本次开跑于2026-09-23_074438.txt 行「盘20　判决 1.000／59.0　4164 格」（同上）" },
        new() { DiscMm = 20, Fine = false, Hot = 6.081,  Cold = 2.445,  Flux = -0.265, Source = "deliverable/网格修复_门f_现役数_W08_N树加F6参照_本次开跑于2026-09-23_074438.txt 行「盘20　导航 2.000／59.0　1116 格」（同上）" },
    };

    internal static DesignSpec Design(double discMm)
    {
        var d = R48LW08NavDesign.Build();
        d.FlangeInsulated = true; d.FlangeInsulMm = discMm; d.DiscInsulMm = Array.Empty<double>();
        return d;
    }

    /// <summary>
    /// 细网格 = MeshVerify.RequiredMeshFor（1.000／59.0）；导航 = FineMm 0（Solver.ApplyCaseMesh 只统一细区半径 = RequiredMeshFor 的半径，2.0／59.0）。
    /// ★ 2026-09-23：导航从「整线算例缺省 2.0／50」改成与 N 路门 f 同一口径 —— 参照换成了 N 门 f 的数，网格要跟参照走（见 <see cref="Refs"/> 的变因）。
    /// </summary>
    internal static SolverOptions Mesh(bool fine, DesignSpec d)
    {
        var (f, r) = MeshVerify.RequiredMeshFor(d);
        return fine ? new SolverOptions { FineMm = f, FineRadiusMm = r }
                    : new SolverOptions { FineMm = 0, FineRadiusMm = r };
    }

    internal sealed class Row
    {
        public string Tag = ""; public LineResult R = new(); public double Sec; public bool Cut; public int Cells;
    }

    internal static Row Run(string tag, DesignSpec d, SolverOptions mesh, TimeSpan cap, IProgress<string> probe, bool measureJac = true)
    {
        var lc = d.Clone().BuildCase(new DesignInputs());
        Solver.ApplyCaseMesh(lc, mesh);
        lc.MeasureJacobianAmp = measureJac;
        var sw = Stopwatch.StartNew();
        LineResult r; bool cut = false;
        probe.Report($"── 开始 ②（{tag}）");
        using (var cts = new CancellationTokenSource(cap))
        {
            try { r = LineRunner.Run(lc, probe, cts.Token); }
            catch (OperationCanceledException) { r = new LineResult { Ok = false, Message = $"被跑前写死的时间闸（{cap.TotalMinutes:0} 分钟）切断" }; cut = true; }
            catch (Exception ex) { r = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
        }
        sw.Stop();
        probe.Report($"── ② 结束（{tag}），{sw.Elapsed.TotalSeconds:0} s，{r.CoupleRounds} 轮，容差 {r.CoupleTolKUsed:0.0000} K，认证误差 {r.CertErrK:0.0000} K");
        return new Row { Tag = tag, R = r, Sec = sw.Elapsed.TotalSeconds, Cut = cut, Cells = r.MeshCells };
    }

    internal static double V(LineResult r, string key) => R48LColdUnderTcConditioningTests.ValueOf(r, key);
    internal static string F(double v, string fmt = "0.###") => double.IsNaN(v) ? "—" : v.ToString(fmt, CultureInfo.InvariantCulture);

    internal static string Line(Row q)
        => $"{q.Tag}\t{(q.Cut ? "**被时间闸切断**" : q.R.Converged.ToString())}\t{q.R.CoupleRounds}\t{q.Sec:0}\t{(q.R.CoupleRounds > 0 ? (q.Sec / q.R.CoupleRounds).ToString("0.0") : "—")}\t"
         + $"{F(q.R.CoupleTolKUsed, "0.0000")}\t{F(q.R.CertErrK, "0.0000")}\t{F(q.R.CoupleAmpUsed, "0.0")}={F(q.R.CoupleAmpClosed, "0.0")}×1.1|{F(q.R.CoupleAmpJacobian, "0.0")}\t{q.R.JacobianAmpSec:0.0}\t{q.Cells}\t"
         + $"{F(V(q.R, LineResult.Key.HotOverTc))}\t{F(V(q.R, LineResult.Key.ColdUnderTc))}\t{F(V(q.R, LineResult.Key.NetFlux))}\t{F(q.R.TotalMassG, "0.##")}\t{(q.R.AllOk ? "全判据通过" : "有判据不过或判不了")}";

    internal const string Header = "行\t耦合收敛\t外层轮数\t耗时 s\t每轮 s\t实际容差 K\t认证误差 K\t放大=闭式×1.1|雅可比\t量雅可比 s\t单元\t最热铂高出热偶读数 K\t管根低于热偶读数 K\t管孔净流入 W\t铂重 g\t本工况结论";

    internal static void Head(Action<string> W, string title, string live)
    {
        W(title);
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Fable 5.1");
        W($"设计 = {R48LW08NavDesign.Source}（圆盘保温按行写死；本树默认 {DesignSpec.W08.FlangeInsulMm:0.#} mm）");
        W("停机口径 = 生产默认（绝对目标 " + new LineCase().CoupleTolK.ToString("0.###", CultureInfo.InvariantCulture) + " K，不按裕度）；放大 = max(闭式 × 1.1, 实测雅可比)；认证误差 = 放大 × 停机残差");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
    }
}

[Trait("速度", "慢")]
public class R48MStopTolCostTests
{
    private readonly ITestOutputHelper _o;
    public R48MStopTolCostTests(ITestOutputHelper o) { _o = o; }

    /// <summary>门 (c)：细网格整线一次（生产默认）≤ 1.5 × 146 s；另跑一行「不量雅可比」把雅可比的成本单独量出来（只量不判）。</summary>
    [Fact]
    public void 门_细网格生产默认一次整线_不超过0点10那跑的1点5倍()
    {
        string file = DeliverableOut.Stamped("R48_M_停机容差绝对目标_细网格成本.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_M_成本_{DeliverableOut.RunStamp}_进行中.log");
        var probe = new R48LSolveOrderTests.LiveProbe(live);
        var sb = new StringBuilder(); void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        R48MCostKit.Head(W, "R48 M 路　**细网格整线一次的成本（生产默认）：不超过 §0.-10 ④ 那一跑的 1.5 倍**", live);
        W($"判读（跑前写死）：生产默认那一行耗时 ≤ {R48MCostKit.CostFactorCap:0.#} × {R48MCostKit.RefFineSec:0} s = {R48MCostKit.CostFactorCap * R48MCostKit.RefFineSec:0} s（参照 deliverable/R48_L_耦合容差收紧_细网格_本次开跑于2026-09-17_224252.txt ④）。");
        W("⚠ 墙钟门：同一台机器上别的工作树在跑会把它撞红；撞红照报，轮数与每轮秒数一并印出供判读。「不量雅可比」那一行只量成本不判。");
        W("");
        var d = R48MCostKit.Design(DesignSpec.W08.FlangeInsulMm);
        var mesh = R48MCostKit.Mesh(true, d);
        W($"网格 = 细网格（{mesh.FineMm:0.000} mm／半径 {mesh.FineRadiusMm:0.0}）　圆盘保温 = {d.FlangeInsulMm:0.#} mm（本树默认）");
        W(R48MCostKit.Header); Flush();
        var prod = R48MCostKit.Run("生产默认（量雅可比）", d, mesh, TimeSpan.FromMinutes(60), probe);
        W(R48MCostKit.Line(prod)); Flush();
        var noJac = R48MCostKit.Run("对照：不量雅可比（放大只用闭式 × 1.1）", d, mesh, TimeSpan.FromMinutes(60), probe, measureJac: false);
        W(R48MCostKit.Line(noJac)); Flush();
        W("");
        W($"成本：生产默认 {prod.Sec:0} s（{prod.R.CoupleRounds} 轮）对参照 146 s（34 轮）= {prod.Sec / R48MCostKit.RefFineSec:0.00} 倍；量雅可比 {prod.R.JacobianAmpSec:0.0} s。");
        foreach (var q in new[] { prod, noJac })
            foreach (var n in q.R.Notes.Where(n => n.Contains("轮收敛") || n.Contains("停机放大口径") || n.Contains("未收敛")))
                W($"· {q.Tag}：{n}");
        W($"── 结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Flush(); _o.WriteLine($"报告：{file}");
        Assert.False(prod.Cut, "被时间闸切断");
        Assert.True(prod.R.Converged, "生产默认没收敛：" + prod.R.Message);
        Assert.True(prod.Sec <= R48MCostKit.CostFactorCap * R48MCostKit.RefFineSec,
            $"细网格一次整线 {prod.Sec:0} s > {R48MCostKit.CostFactorCap * R48MCostKit.RefFineSec:0} s（{prod.R.CoupleRounds} 轮、每轮 {prod.Sec / Math.Max(1, prod.R.CoupleRounds):0.0} s、量雅可比 {prod.R.JacobianAmpSec:0.0} s）—— 见 {file}");
    }

    /// <summary>门 (d)：四行对拍，两条温度硬安全线位移 ≤ 0.05 K；瓦的那条只报。</summary>
    [Fact]
    public void 门_四行细网格对拍_温度硬安全线位移不超0点05K()
    {
        string file = DeliverableOut.Stamped("R48_M_停机容差绝对目标_四行对拍.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_M_四行_{DeliverableOut.RunStamp}_进行中.log");
        var probe = new R48LSolveOrderTests.LiveProbe(live);
        var sb = new StringBuilder(); void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        R48MCostKit.Head(W, "R48 M 路　**四行细网格对拍：10/20 mm × 细/导航，换了停机口径之后判据值位移多少**", live);
        W($"判读（跑前写死）：最热铂高出热偶读数／管根低于热偶读数 对参照的位移 |Δ| ≤ {R48MCostKit.ShiftCapK:0.##} K；管孔净流入（W）只报不判；超了查因，不挪门槛。");
        W("参照每行指回它的文件（见下表「参照出处」列）。");
        W("");
        W("行\t圆盘 mm\t网格\t本次 最热铂\t参照\tΔ\t本次 管根\t参照\tΔ\t本次 净流入\t参照\tΔ\t轮数\t容差\t认证误差\t耗时 s\t单元\t参照出处");
        Flush();
        var fails = new List<string>();
        var rows = new List<(R48MCostKit.Ref Ref, R48MCostKit.Row Row)>();
        foreach (var rf in R48MCostKit.Refs)
        {
            var d = R48MCostKit.Design(rf.DiscMm);
            var mesh = R48MCostKit.Mesh(rf.Fine, d);
            var q = R48MCostKit.Run($"{rf.DiscMm:0} mm／{(rf.Fine ? "细网格" : "导航")}", d, mesh, TimeSpan.FromMinutes(60), probe);
            rows.Add((rf, q));
            double hot = R48MCostKit.V(q.R, LineResult.Key.HotOverTc), cold = R48MCostKit.V(q.R, LineResult.Key.ColdUnderTc), flux = R48MCostKit.V(q.R, LineResult.Key.NetFlux);
            W($"{q.Tag}\t{rf.DiscMm:0}\t{(rf.Fine ? "细网格" : "导航")}\t{R48MCostKit.F(hot)}\t{rf.Hot:0.###}\t{R48MCostKit.F(hot - rf.Hot, "+0.000;-0.000")}\t"
              + $"{R48MCostKit.F(cold)}\t{rf.Cold:0.###}\t{R48MCostKit.F(cold - rf.Cold, "+0.000;-0.000")}\t{R48MCostKit.F(flux)}\t{rf.Flux:0.###}\t{R48MCostKit.F(flux - rf.Flux, "+0.000;-0.000")}\t"
              + $"{q.R.CoupleRounds}\t{R48MCostKit.F(q.R.CoupleTolKUsed, "0.0000")}\t{R48MCostKit.F(q.R.CertErrK, "0.0000")}\t{q.Sec:0}\t{q.Cells}\t{rf.Source}");
            Flush();
            if (q.Cut || !q.R.Converged) fails.Add($"{q.Tag}：{(q.Cut ? "被时间闸切断" : "没收敛")}");
            else
            {
                if (!(Math.Abs(hot - rf.Hot) <= R48MCostKit.ShiftCapK)) fails.Add($"{q.Tag}：最热铂位移 {hot - rf.Hot:+0.000;-0.000} K");
                if (!(Math.Abs(cold - rf.Cold) <= R48MCostKit.ShiftCapK)) fails.Add($"{q.Tag}：管根位移 {cold - rf.Cold:+0.000;-0.000} K");
            }
        }
        W("");
        W("── 收敛那一句（原文）");
        foreach (var (rf, q) in rows)
            foreach (var n in q.R.Notes.Where(n => n.Contains("轮收敛") || n.Contains("停机放大口径") || n.Contains("未收敛") || n.Contains("判不了那么细")))
                W($"· {q.Tag}：{n}");
        W("");
        W(fails.Count == 0 ? "判读：四行两条温度硬安全线位移都 ≤ 0.05 K —— 过。" : "判读：**不过** —— " + string.Join("；", fails));
        W($"── 结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Flush(); _o.WriteLine($"报告：{file}");
        Assert.True(fails.Count == 0, string.Join("；", fails) + $" —— 见 {file}");
    }
}
