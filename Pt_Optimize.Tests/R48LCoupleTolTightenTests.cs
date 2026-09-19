using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路：**把外层法兰↔管耦合的停机容差收紧到比判据裕度还细** —— 2026-09-17，Opus 5
//
//  ══ 病（上一位实测，deliverable/R48_L_管根判据_最后一位敏感度_本次开跑于2026-09-17_151814.txt）
//     舌保温输入改 1 ulp（相对 1.7e−16）⇒「管根低于热偶读数」4.511 → 4.885 K（动 0.374 K），
//     而这条硬安全线的裕度只有 0.489 K；共用片那两个接头在 4.42↔4.92 之间摆（0.50 K），
//     与耦合剩余误差估计（0.430↔0.933 K）同量级 ⇒ **这个数不可复现**。
//  ══ 根子：外层耦合停机容差 CoupleTolK = 1 K，比这条判据的裕度（0.1～0.5 K）**粗**。
//     判据的分辨率必须**比停机容差细**，否则「过」与「不过」分不开。
//
//  ══ 本轮做什么（生产改动见 LineCase.CoupleTolK / LineRunner.CoupleTolKFor）
//     停机容差改成 tol = min(上限 0.1 K, max(下限 0.02 K, 0.1 × 当前最小硬安全线温度裕度))。
//     **两种口径都实测**：新上限当常数用 ／ 判据相关（生产默认）；
//     对照组是旧口径常数 1 K（= 改前行为，也是注射用的那一份）。
//     另一半同样重要：**无法兰基线那一层不跟着收**（它只进参考量「法兰增量温降」）——
//     实测两层一起收 293 s、只收主环 111 s，而三个交付判据逐位相同。
//
//  ══ 判读（**跑前写死在代码里，跑完不挪**）
//     · 可复现：同一组 1 ulp 扰动六次，「管根低于热偶读数」的**摆幅（max−min）≤ 0.05 K**（R48LCoupleTolKit.SwingCapK）。
//       摆幅仍大 ⇒ **不许**写成「收紧了就好了」，要如实报并往下一层查（段解停机 0.005/0.01 K、Anderson）。
//     · 注射（同一次运行里把容差改回 1 K 再跑同一组六次）：摆幅**必须大于** 0.05 K ——
//       否则这道门守的是空气（那说明摆幅本来就小，与容差无关），本测试当场判自己无效。
//     · 成本如实报：轮数、耗时、实际用的容差，三样都进表；快了慢了都照写。
//     · 时间闸跑前写死：超了就切断、报「被时间闸切断」，不许装作跑完了，也不许为了跑完把容差放回去。
//
//  ⚠ 四个 Fact 分成四个类（导航成本／细网格成本／空管成本／可复现），是为了能**分别开跑**
//    （测试名是中文，命令行只筛得动类名）。
// ════════════════════════════════════════════════════════════════════════════

/// <summary>三个测试共用的那一份：口径怎么设、怎么跑一次、怎么算摆幅。**不许各抄一遍。**</summary>
internal static class R48LCoupleTolKit
{
    /// <summary>判读门槛（跑前写死）：1 ulp 扰动六次，「管根低于热偶读数」的摆幅上限 K。</summary>
    internal const double SwingCapK = 0.05;

    /// <summary>改前那个常数容差 K（注射用的那一份；生产默认已不是它）。</summary>
    internal const double LegacyTolK = 1.0;

    internal static readonly TimeSpan NavCap = TimeSpan.FromMinutes(40);
    internal static readonly TimeSpan FineCap = TimeSpan.FromMinutes(120);

    /// <summary>四种停机容差口径（主环 × 基线两层都写明）。</summary>
    internal enum TolMode
    {
        /// <summary>改前：主环常数 1 K、基线 1 K。</summary>
        LegacyConst,
        /// <summary>主环 = 新上限当常数用（不按裕度）；基线按生产口径（它服务的是参考量）。</summary>
        NewConst,
        /// <summary>主环 = 判据相关；基线**也一起**收到上限（上一跑那一行的口径）。</summary>
        FromMarginTightBase,
        /// <summary>**生产默认**：主环判据相关；基线按它自己服务的那条判据（参考量，限值 10 K ⇒ 1 K）。</summary>
        Production,
    }

    /// <summary>
    /// ★ 四种口径怎么设，**全仓只有这一份**（三个测试共用）。
    /// ⚠ 「新口径当常数用」这一支**不写数**：上限就是生产默认 <see cref="LineCase.CoupleTolK"/>，
    ///   手抄一个 0.1 进来，生产默认哪天改了这里还是 0.1，表就成了假的（门不许手抄生产配方）。
    /// ⚠ 两层要分开写：实测收紧的机时几乎全在**基线**那一层，而基线一个交付判据都不卡。
    /// </summary>
    internal static void ApplyTol(LineCase lc, TolMode m)
    {
        switch (m)
        {
            case TolMode.LegacyConst:
                lc.CoupleTolFromMargin = false; lc.CoupleTolK = LegacyTolK; lc.BaselineTolK = LegacyTolK; break;
            case TolMode.NewConst:
                lc.CoupleTolFromMargin = false; break;
            case TolMode.FromMarginTightBase:
                lc.BaselineTolK = lc.CoupleTolK; break;
            case TolMode.Production: break;   // 生产默认原样，一个字段都不碰
        }
    }

    internal static string TolTag(LineCase lc)
        => (lc.CoupleTolFromMargin
            ? $"主环 判据相关（上限 {lc.CoupleTolK:0.###}／下限 {lc.CoupleTolFloorK:0.###}／比例 {lc.CoupleTolMarginFrac:0.##}）"
            : $"主环 常数 {lc.CoupleTolK:0.###} K")
         + $"｜基线 {lc.BaselineTolK:0.###} K";

    internal static string TagOf(TolMode m) => m switch
    {
        TolMode.LegacyConst => $"① 旧口径：主环 {LegacyTolK:0.#} K／基线 {LegacyTolK:0.#} K（改前行为）",
        TolMode.NewConst => "② 新上限当常数用（主环收到上限，不按裕度）",
        TolMode.FromMarginTightBase => "③ 主环判据相关 + 基线也一起收到上限",
        _ => "④ **生产默认**：主环判据相关 + 基线按它自己的判据（1 K）",
    };

    internal sealed class Row
    {
        public string Tag = "", TolSet = "";
        public LineResult R = new();
        public double Sec;
        public bool Cut;
    }

    internal static Row RunOne(Action<string> W, string tag, DesignSpec d, DesignInputs p, TolMode m,
                               SolverOptions? mesh, TimeSpan cap, R48LSolveOrderTests.LiveProbe probe,
                               bool emptyTube = false)
    {
        var lc = d.Clone().BuildCase(p, emptyTube: emptyTube);
        if (mesh is not null) Solver.ApplyCaseMesh(lc, mesh);
        ApplyTol(lc, m);
        string tolSet = TolTag(lc);
        var sw = Stopwatch.StartNew();
        LineResult r; bool cut = false;
        probe.Report($"── 开始 ②（{tag}｜容差口径 {tolSet}）");
        using (var cts = new CancellationTokenSource(cap))
        {
            try { r = LineRunner.Run(lc, probe, cts.Token); }
            catch (OperationCanceledException)
            { r = new LineResult { Ok = false, Message = $"被跑前写死的时间闸（{cap.TotalMinutes:0} 分钟）切断" }; cut = true; }
            catch (Exception ex) { r = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
        }
        sw.Stop();
        probe.Report($"── ② 结束（{tag}），{sw.Elapsed.TotalSeconds:0} s，{r.CoupleRounds} 轮，容差 {r.CoupleTolKUsed:0.0000} K，"
                   + $"管根 {Cold(r):0.000}");
        W($"── {tag}｜容差口径 {tolSet}");
        W($"　实际用的容差 {Fmt(r.CoupleTolKUsed)} K／外层 {r.CoupleRounds} 轮／耗时 {sw.Elapsed.TotalSeconds:0} s／"
          + $"耦合收敛 {(cut ? "**被时间闸切断**" : r.Converged.ToString())}／剩余误差估计 {Fmt(r.CoupleRemainK)} K");
        W($"　管根低于热偶读数 {Fmt(Cold(r))} K（位置 {WhereOf(r, LineResult.Key.ColdUnderTc)}）"
          + $"／最热铂 {Fmt(ValueOf(r, LineResult.Key.HotOverTc))} K"
          + $"／净流入 {Fmt(ValueOf(r, LineResult.Key.NetFlux))} W"
          + $"／铂重 {Fmt(r.TotalMassG)} g／{(r.AllOk ? "全判据通过" : "有判据不过或判不了")}"
          + (r.Ok ? "" : $"／**整线解没解出来**：{r.Message}"));
        W("");
        return new Row { Tag = tag, TolSet = tolSet, R = r, Sec = sw.Elapsed.TotalSeconds, Cut = cut };
    }

    // 判据的读口只有一份（R48LColdUnderTcConditioningTests 那边的 Val/Where/PerJoint），这里只转发
    internal static double ValueOf(LineResult r, string key) => R48LColdUnderTcConditioningTests.ValueOf(r, key);
    internal static string WhereOf(LineResult r, string key) => R48LColdUnderTcConditioningTests.WhereOf(r, key);
    internal static string PerJointOf(LineResult r) => R48LColdUnderTcConditioningTests.PerJointOf(r);
    internal static double Cold(LineResult r) => ValueOf(r, LineResult.Key.ColdUnderTc);

    internal static double Swing(List<Row> rows, string key = "")
    {
        string k = key.Length == 0 ? LineResult.Key.ColdUnderTc : key;
        var vals = rows.Select(q => ValueOf(q.R, k)).Where(v => !double.IsNaN(v)).ToArray();
        return vals.Length == 0 ? double.NaN : vals.Max() - vals.Min();
    }

    internal static string Ratio(double now, double before) => before > 0 ? $"{now / before:0.0} 倍" : "—";

    internal static string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("0.###");

    internal static void Flush(StringBuilder sb, string file)
        => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

    internal static void Finish(ITestOutputHelper o, StringBuilder sb, string file, Stopwatch totalSw)
    {
        totalSw.Stop();
        sb.AppendLine();
        sb.AppendLine($"── 总耗时 {totalSw.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        sb.AppendLine("出处：带玻璃稳态 = LineRunner.Run；设计 = R48LW08NavDesign.Build（照端到端输出表复原）；"
                    + "容差 = LineRunner.CoupleTolKFor（全仓唯一读口），轮数与容差直接读 LineResult.CoupleRounds／CoupleTolKUsed。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        o.WriteLine(sb.ToString());
        Console.WriteLine(sb.ToString());
    }

    /// <summary>收紧前/后的成本对照：同一份设计、同一张网格，只换容差口径（导航与细网格两个类共用这一份）。</summary>
    internal static void CostRun(ITestOutputHelper o, string meshTag, SolverOptions? mesh, TimeSpan cap,
                                string shortTag, TolMode[] modes, bool emptyTube = false)
    {
        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_耦合容差收紧_{shortTag}_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        string live = Path.Combine(Path.GetTempPath(), $"R48_L_容差_{shortTag}_{stamp}_进行中.log");
        var probe = new R48LSolveOrderTests.LiveProbe(live);

        W("R48 L 路　**外层耦合停机容差：收紧前 / 后**（同一份设计、同一张网格，只换容差口径）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("");
        W("═══════ 病与根子 ═══════");
        W("1 ulp 的输入差把「管根低于热偶读数」挪 0.374 K，而它的裕度只有 0.489 K");
        W("（出处 deliverable/R48_L_管根判据_最后一位敏感度_本次开跑于2026-09-17_151814.txt）。");
        W("根子：停机容差 1 K **比判据的裕度还粗** ⇒ 判据的分辨率必须比停机容差细。");
        W("");
        W("═══════ 本跑（同一个进程、同一份代码、同一份设计、同一张网格）═══════");
        W($"设计 = {R48LW08NavDesign.Source}");
        W($"网格 = {meshTag}");
        // ★ 2026-09-18，Opus 5（合并复核查出）：**圆盘保温厚度必须印进证据头**。
        //   §0.-11 把整线圆盘保温的缺省从 20 改成 10 mm；这份探针原先一个字都没写它 ——
        //   同一条「管根低于热偶读数」在 20 mm 档是 3.785 K、10 mm 档是 19.004 K，
        //   头里不写，拿到文件的人无从知道手上这份是哪一档（「每个数都要能指回出处」）。
        //   ⚠ 三个数都不许手抄：整线走 DesignSpec.WholeLineDiscInsulMm（BuildCase 造算例取的同一处），
        //     逐片走 DesignSpec.DiscInsulMmOf（配套清单与安装报告取的同一处），
        //     上限走 WrapLimits.JointZoneMaxMm —— 缺省或层厚哪天再改，这两行跟着改。
        {
            var dHead = R48LW08NavDesign.Build();
            string perDisc = string.Join("／", Enumerable.Range(0, dHead.FlangeCount)
                                                        .Select(j => $"{dHead.DiscInsulMmOf(j):0.###}"));
            W($"圆盘保温 = 整线 {dHead.WholeLineDiscInsulMm:0.###} mm（包 = {dHead.FlangeInsulated}）；"
              + $"逐片（入口→出口）{perDisc} mm；接合区上限 {WrapLimits.JointZoneMaxMm:0.#} mm");
            W($"舌保温（逐片，入口→出口）= {string.Join("／", dHead.TabInsulMm.Select(v => $"{v:0.###}"))} mm；"
              + $"管保温 = {dHead.TubeInsulMm:0.###} mm");
        }
        W($"判据 = {(emptyTube ? "③ 空管到温稳态" : "② 带玻璃稳态")}（LineRunner.Run，工况位 EmptyTube = {emptyTube}）；"
          + $"时间闸（跑前写死）{cap.TotalMinutes:0} 分钟／每跑");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        W("── 判读（跑前写死）：成本（轮数／耗时）如实报；收紧后不收敛或被时间闸切断 ⇒ 照报，**不许**把容差放回去凑一个好看的表。");
        W("");

        var rows = new List<Row>();
        foreach (var m in modes)
        {
            rows.Add(RunOne(W, TagOf(m), R48LW08NavDesign.Build(), p, m, mesh, cap, probe, emptyTube));
            Flush(sb, file);
        }

        W("═══════ 并列（只换容差口径）═══════");
        W("容差口径\t设的容差\t实际用的容差 K\t外层轮数\t耗时 s\t耦合收敛\t剩余误差估计 K\t管根低于热偶读数 K\t位置\t最热铂高出热偶读数 K\t管孔净流入 W\t铂重 g\t本工况结论");
        foreach (var q in rows)
            W($"{q.Tag}\t{q.TolSet}\t{Fmt(q.R.CoupleTolKUsed)}\t{q.R.CoupleRounds}\t{q.Sec:0}\t{(q.Cut ? "**被时间闸切断**" : q.R.Converged.ToString())}\t"
              + $"{Fmt(q.R.CoupleRemainK)}\t{Fmt(Cold(q.R))}\t{WhereOf(q.R, LineResult.Key.ColdUnderTc)}\t"
              + $"{Fmt(ValueOf(q.R, LineResult.Key.HotOverTc))}\t{Fmt(ValueOf(q.R, LineResult.Key.NetFlux))}\t"
              + $"{Fmt(q.R.TotalMassG)}\t{(q.R.AllOk ? "全判据通过" : "有判据不过或判不了")}");
        W("");
        W("── 逐接头的「管根低于热偶读数」");
        foreach (var q in rows) W($"{q.Tag}：{PerJointOf(q.R)}");
        W("");
        W("── 收敛/未收敛那一句（原文，容差与轮数都在里面）");
        foreach (var q in rows)
        {
            W($"· {q.Tag}");
            foreach (var n in q.R.Notes.Where(n => n.Contains("外层耦合") || n.Contains("分辨率地板") || n.Contains("基线")))
                W("　" + n);
        }
        W("");
        W("═══════ 成本（按跑前写死的：如实报）═══════");
        W("⚠ 两层要分开读：**无法兰基线**那一层只进参考量「法兰增量温降」（限值 10 K，不卡交付），");
        W("　**主环**那一层才是两条硬安全线的分辨率。③ 与 ④ 的差**只有基线那一层的容差**。");
        var baseRow = rows[0];
        foreach (var q in rows.Skip(1))
            W($"{q.Tag} 对 {baseRow.Tag}：轮数 {baseRow.R.CoupleRounds} → {q.R.CoupleRounds}（{Ratio(q.R.CoupleRounds, baseRow.R.CoupleRounds)}），"
              + $"耗时 {baseRow.Sec:0} → {q.Sec:0} s（{Ratio(q.Sec, baseRow.Sec)}），"
              + $"管根 {Fmt(Cold(baseRow.R))} → {Fmt(Cold(q.R))} K");
        W("");
        W($"⚠ 这 {rows.Count} 行来自同一次运行、同一个进程、同一份代码，不拼两份仪器输出。");

        Finish(o, sb, file, totalSw);
        Assert.Equal(modes.Length, rows.Count);
        Assert.All(rows, q => Assert.False(q.Cut, $"{q.Tag} 被跑前写死的时间闸（{cap.TotalMinutes:0} 分钟）切断 —— 见输出文件"));
    }
}

/// <summary>收紧前/后的成本对照 —— **导航网格**。</summary>
[Trait("速度", "慢")]
public class R48LCoupleTolTightenTests
{
    private readonly ITestOutputHelper _o;
    public R48LCoupleTolTightenTests(ITestOutputHelper o) { _o = o; }

    [Fact]
    public void 外层耦合容差_收紧前后_导航网格_成本与判据值()
        => R48LCoupleTolKit.CostRun(_o, "导航网格（整线算例缺省 2.0 mm／细区半径 50.0）",
                                    null, R48LCoupleTolKit.NavCap, "导航",
                                    new[] { R48LCoupleTolKit.TolMode.LegacyConst, R48LCoupleTolKit.TolMode.NewConst,
                                            R48LCoupleTolKit.TolMode.FromMarginTightBase, R48LCoupleTolKit.TolMode.Production });
}

/// <summary>收紧前/后的成本对照 —— **细网格**（网格无关口径那张）。</summary>
[Trait("速度", "慢")]
public class R48LCoupleTolFineTests
{
    private readonly ITestOutputHelper _o;
    public R48LCoupleTolFineTests(ITestOutputHelper o) { _o = o; }

    [Fact]
    public void 外层耦合容差_收紧前后_细网格_成本与判据值()
    {
        var (fine, radius) = MeshVerify.RequiredMeshFor(DesignSpec.W08);
        // 细网格上只跑「改前 / 新口径常数 / 生产默认」三行：
        // 「基线那一层收紧要多花多少」这个问题已经在导航网格上量过（同一个问题不必在最贵的网格上再答一遍）。
        R48LCoupleTolKit.CostRun(_o, $"细网格（网格无关口径 {fine:0.000} mm／细区半径 {radius:0.0}）",
                                 new SolverOptions { FineMm = fine, FineRadiusMm = radius },
                                 R48LCoupleTolKit.FineCap, "细网格",
                                 new[] { R48LCoupleTolKit.TolMode.LegacyConst, R48LCoupleTolKit.TolMode.NewConst,
                                         R48LCoupleTolKit.TolMode.Production });
    }
}

/// <summary>
/// 收紧前/后的成本对照 —— **空管到温工况**。
/// 为什么单跑这一档：分工况口径下，空管态里「最热铂高出热偶读数」「管根低于热偶读数」都是**参考量**
/// ⇒ 读口挑不到硬安全线温度判据 ⇒ 容差落在**上限** 0.1 K（仍比改前的 1 K 细十倍）。
/// 这一支的机时改前没人量过，不许拿带玻璃那一档的数替它说话。
/// </summary>
[Trait("速度", "慢")]
public class R48LCoupleTolEmptyTubeTests
{
    private readonly ITestOutputHelper _o;
    public R48LCoupleTolEmptyTubeTests(ITestOutputHelper o) { _o = o; }

    [Fact]
    public void 外层耦合容差_收紧前后_空管到温_成本与判据值()
        => R48LCoupleTolKit.CostRun(_o, "导航网格（整线算例缺省 2.0 mm／细区半径 50.0）",
                                    null, R48LCoupleTolKit.NavCap, "空管",
                                    new[] { R48LCoupleTolKit.TolMode.LegacyConst, R48LCoupleTolKit.TolMode.Production },
                                    emptyTube: true);
}

/// <summary>可复现门：1 ulp 扰动六次，摆幅必须 ≤ 门槛；同一次运行里把容差改回 1 K ⇒ 必须超门槛。</summary>
[Trait("速度", "慢")]
public class R48LCoupleTolReproducibleTests
{
    private readonly ITestOutputHelper _o;
    public R48LCoupleTolReproducibleTests(ITestOutputHelper o) { _o = o; }

    [Fact]
    public void 管根判据_一个ulp扰动六次_收紧后摆幅必须小于门槛_改回1K必须超门槛()
    {
        const double cap = R48LCoupleTolKit.SwingCapK;
        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_管根判据_收紧后可复现_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        string live = Path.Combine(Path.GetTempPath(), $"R48_L_可复现_{stamp}_进行中.log");
        var probe = new R48LSolveOrderTests.LiveProbe(live);

        W("R48 L 路　**收紧停机容差之后，「管根低于热偶读数」还会不会被 1 ulp 挪动**");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("");
        W("═══════ 判读（**跑前写死在代码里，跑完不挪**）═══════");
        W($"· 收紧后（生产默认 = 判据相关容差）：六次的「管根低于热偶读数」摆幅 max−min **≤ {cap:0.###} K** ⇒ 过。");
        W("· 摆幅仍大 ⇒ 如实报「没治好」，并往下一层查（段解自己的停机 0.005/0.01 K、Anderson），**不许**写成「收紧了就好了」。");
        W($"· 注射：同一次运行里把容差改回 {R48LCoupleTolKit.LegacyTolK:0.#} K，同一组六次的摆幅**必须大于** {cap:0.###} K ——");
        W("　否则这道门守的是空气（摆幅本来就小、与容差无关），本测试当场判自己无效。");
        W("· 六份设计的造法 = R48LColdUnderTcConditioningTests.Variants()（全仓唯一一份，两个测试共用）。");
        W("");
        W($"网格：导航网格（整线算例缺省）；设计 = {R48LW08NavDesign.Source}");
        W($"进度活页（临时，非交付物）：{live}");
        W("");

        var tight = new List<R48LCoupleTolKit.Row>();
        W("═══════ A 组：收紧后（生产默认 = 判据相关容差）═══════");
        foreach (var v in R48LColdUnderTcConditioningTests.Variants())
        {
            tight.Add(R48LCoupleTolKit.RunOne(W, v.Tag, v.D, p, R48LCoupleTolKit.TolMode.Production,
                                              null, R48LCoupleTolKit.NavCap, probe));
            R48LCoupleTolKit.Flush(sb, file);
        }
        double swingTight = R48LCoupleTolKit.Swing(tight);
        W($"A 组摆幅（max−min）= **{swingTight:0.000} K**（门槛 {cap:0.###}）");
        W("");

        var legacy = new List<R48LCoupleTolKit.Row>();
        W($"═══════ B 组：注射 —— 容差改回 {R48LCoupleTolKit.LegacyTolK:0.#} K（= 改前行为）═══════");
        foreach (var v in R48LColdUnderTcConditioningTests.Variants())
        {
            legacy.Add(R48LCoupleTolKit.RunOne(W, v.Tag, v.D, p, R48LCoupleTolKit.TolMode.LegacyConst,
                                               null, R48LCoupleTolKit.NavCap, probe));
            R48LCoupleTolKit.Flush(sb, file);
        }
        double swingLegacy = R48LCoupleTolKit.Swing(legacy);
        W($"B 组摆幅（max−min）= **{swingLegacy:0.000} K**（注射后必须 > {cap:0.###}，否则门守的是空气）");
        W("");

        W("═══════ 并列 ═══════");
        W("跑法\tA 收紧后 管根 K\tA 位置\tA 容差 K\tA 轮数\tA 耗时 s\tB 改回1K 管根 K\tB 位置\tB 容差 K\tB 轮数\tB 耗时 s");
        for (int i = 0; i < tight.Count; i++)
            W($"{tight[i].Tag}\t{R48LCoupleTolKit.Fmt(R48LCoupleTolKit.Cold(tight[i].R))}\t"
              + $"{R48LCoupleTolKit.WhereOf(tight[i].R, LineResult.Key.ColdUnderTc)}\t"
              + $"{R48LCoupleTolKit.Fmt(tight[i].R.CoupleTolKUsed)}\t{tight[i].R.CoupleRounds}\t{tight[i].Sec:0}\t"
              + $"{R48LCoupleTolKit.Fmt(R48LCoupleTolKit.Cold(legacy[i].R))}\t"
              + $"{R48LCoupleTolKit.WhereOf(legacy[i].R, LineResult.Key.ColdUnderTc)}\t"
              + $"{R48LCoupleTolKit.Fmt(legacy[i].R.CoupleTolKUsed)}\t{legacy[i].R.CoupleRounds}\t{legacy[i].Sec:0}");
        W("");
        W("── 逐接头的「管根低于热偶读数」（A 组）");
        foreach (var q in tight) W($"{q.Tag}：{R48LCoupleTolKit.PerJointOf(q.R)}");
        W("── 逐接头的「管根低于热偶读数」（B 组）");
        foreach (var q in legacy) W($"{q.Tag}：{R48LCoupleTolKit.PerJointOf(q.R)}");
        W("");
        W("── 另外两条硬安全线的摆幅（对照：上一轮实测它们不跳）");
        W($"最热铂高出热偶读数：A {R48LCoupleTolKit.Swing(tight, LineResult.Key.HotOverTc):0.000} K／"
          + $"B {R48LCoupleTolKit.Swing(legacy, LineResult.Key.HotOverTc):0.000} K");
        W($"管孔净流入：A {R48LCoupleTolKit.Swing(tight, LineResult.Key.NetFlux):0.000} W／"
          + $"B {R48LCoupleTolKit.Swing(legacy, LineResult.Key.NetFlux):0.000} W");
        W("");
        W("═══════ 判读（按跑前写死的那几条）═══════");
        W($"A 组摆幅 {swingTight:0.000} K {(swingTight <= cap ? "≤" : "**>**")} 门槛 {cap:0.###} K ⇒ "
          + (swingTight <= cap ? "**过：收紧后这个数可复现到门槛之内**" : "**不过：收紧到这一档还不够，病没治好**"));
        W($"B 组摆幅 {swingLegacy:0.000} K {(swingLegacy > cap ? ">" : "**≤**")} 门槛 ⇒ "
          + (swingLegacy > cap ? "注射有效（改回 1 K 确实红）" : "**注射无效 —— 这道门守的是空气**"));
        if (swingTight > cap)
        {
            W("");
            W("⇒ 没治好。往下一层查（本轮的量在上面那张表里，别再猜）：");
            W("　· 段解自己的停机：SegmentSolver.Profile 的外层 Picard 停在 err < 0.01 K、内层 Bvp1D 停在 Tol = 0.005 K");
            W("　　⇒ G(x) 对 x 只在 ~0.01 K 的分辨率上连续；真残差乘放大（本算例闭式 77～88）⇒ 距离估计的地板约 0.8～0.9 K。");
            W("　· Anderson：外推可以让步长 δ→0 而 x 不在不动点上（LineRunner 里那段注释记着实测）。");
            W("　两条都要各自量一遍再改，**不许**按「大概是它」直接动生产。");
        }
        W("");
        W("⚠ 十二次来自同一次运行、同一个进程、同一份代码，不拼两份仪器输出。");

        R48LCoupleTolKit.Finish(_o, sb, file, totalSw);
        Assert.All(tight.Concat(legacy), q => Assert.False(q.Cut, $"{q.Tag} 被时间闸切断 —— 见输出文件"));
        Assert.True(swingLegacy > cap,
            $"注射（容差改回 {R48LCoupleTolKit.LegacyTolK} K）之后摆幅只有 {swingLegacy:0.000} K ≤ 门槛 {cap} —— 这道门守的是空气，先查仪器再谈结论");
        Assert.True(swingTight <= cap,
            $"收紧之后 1 ulp 扰动下「管根低于热偶读数」的摆幅仍有 {swingTight:0.000} K > 门槛 {cap} K —— 见输出文件");
    }
}
