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
//  (d) 四行细网格对拍（10/20 mm × 细/导航，参照 HANDOVER「一条细网格整线对拍」那张表 = 105752 与 224252／222457 那几跑）：
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

    /// <summary>四行参照（HANDOVER「合并 2026-09-18」§「一条细网格整线对拍」那张表；每行的数指回它的文件）。</summary>
    internal static readonly Ref[] Refs =
    {
        new() { DiscMm = 10, Fine = true,  Hot = -9.259, Cold = 19.004, Flux = 5.793, Source = "deliverable/R48_L_圆盘保温10_重判三关_W08_本次开跑于2026-09-18_105752.txt §「圆盘保温 10 mm ／ 细网格」② 带玻璃稳态（单元 4122，28 轮）" },
        new() { DiscMm = 10, Fine = false, Hot = -9.408, Cold = 19.875, Flux = 6.234, Source = "同上 §「圆盘保温 10 mm ／ 导航」② 带玻璃稳态（单元 1006，31 轮）" },
        new() { DiscMm = 20, Fine = true,  Hot = 4.556,  Cold = 3.785,  Flux = 0.349, Source = "deliverable/R48_L_耦合容差收紧_细网格_本次开跑于2026-09-17_224252.txt ④ 生产默认那一行（34 轮、容差 0.044 K）" },
        new() { DiscMm = 20, Fine = false, Hot = 4.529,  Cold = 4.637,  Flux = 0.655, Source = "deliverable/R48_L_圆盘保温10_重判三关_W08_本次开跑于2026-09-18_105752.txt §「圆盘保温 20 mm ／ 导航」② 带玻璃稳态（单元 1006，36 轮、容差 0.036 K）" },
    };

    internal static DesignSpec Design(double discMm)
    {
        var d = R48LW08NavDesign.Build();
        d.FlangeInsulated = true; d.FlangeInsulMm = discMm; d.DiscInsulMm = Array.Empty<double>();
        return d;
    }

    /// <summary>导航 = 整线算例缺省那张（细区 2.0／半径 50，与 105752 那跑同）；细网格 = MeshVerify.RequiredMeshFor（1.000／59.0）。</summary>
    internal static SolverOptions Mesh(bool fine, DesignSpec d)
    {
        if (!fine) { var nav = new LineCase(); return new SolverOptions { FineMm = nav.MeshFineMm, FineRadiusMm = nav.MeshFineRadiusMm }; }
        var (f, r) = MeshVerify.RequiredMeshFor(d);
        return new SolverOptions { FineMm = f, FineRadiusMm = r };
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
