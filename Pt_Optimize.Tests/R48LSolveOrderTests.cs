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

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路：**半度差的病因** —— 解的次序，还是复原设计？　2026-09-17，Opus 5。
//
//  ══ 症状（HANDOVER §0.-6，上两位留下的开放项）
//  端到端那一跑（Solve → ① → ② → ③ 同一进程）印：管根低于热偶读数 **4.511 K，位置 出口**，耦合剩余 0.430 K。
//  照同一份输出文件的旋钮终值表复原设计、**单独跑 ②**：**4.922 K，位置 HC1|HC2**，耦合剩余 0.661 K。
//  几何决定的量（铂重、法兰截面 J、管 J、圆盘盖得住、舌片自由段）逐位相同 —— 一个「看起来正常的错数」。
//
//  ══ 两个互斥的病因，本测试用实测把它们分开
//    ① **跨解残留状态**：同一个进程里先跑过别的解，② 的结果就变 ⇒ 每次解不是从干净状态起。
//    ② **复原不全**：照表复原出来的根本不是同一份设计（表里缺字段）。
//
//  ══ 怎么分（同一份设计、同一个进程、同一份代码；变的只有「跑 ② 之前做过什么」）
//    (a) 单独跑 ②        —— 进程里第一次场解，最干净
//    (b) 先跑 ① 再跑 ②   —— 端到端那一跑的次序（RunThreeGates 就是 ① 在前）
//    (c) 先跑 ② 再跑 ②   —— 只量「跑过一次 ② 之后」
//    (d) 收尾再跑一次 ②  —— 量整趟下来有没有漂
//  判读（**跑前写死，跑完不挪**）：
//    · 四份结果的**逐位签名**（每条判据的值/限值/过不过/位置 + 铂重 + 耦合剩余 + 网格单元）全等
//      ⇒ 没有跨解残留状态 ⇒ 半度差只可能出在「复原设计」那一步。
//    · 有任何一份不等 ⇒ 就是残留状态，点名是哪一步之后变的。
//    · 复原自证：拿 (a) 与端到端那一跑印的四个数（4.529 / 0.656 / 4.511 出口 / 0.430）比 ——
//      比不上就是复原不全，且能指出差在哪一条。
//  两条判读**互相独立**：残留那条只看四份自比，复原那条只看 (a) 对靶子，不许互相替代。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48LSolveOrderTests
{
    private readonly ITestOutputHelper _o;
    public R48LSolveOrderTests(ITestOutputHelper o) { _o = o; }

    /// <summary>复原自证的比对容差：只比到出处那张表印出来的那几位（0.001）。逐位自比另有一套，那套是真逐位。</summary>
    private const double PrintedTol = 5e-4;

    [Fact]
    public void 半度差_同一设计不同次序()
    {
        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_半度差_解次序对照_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);

        string live = Path.Combine(Path.GetTempPath(), $"R48_L_解次序_{stamp}_进行中.log");
        var probe = new LiveProbe(live);

        W("R48 L 路　**半度差的病因**：解的次序，还是复原设计？");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("");
        W("═══════ 问的是什么 ═══════");
        W("端到端那一跑（同一进程 Solve → ① → ② → ③）印：管根低于热偶读数 4.511 K（位置 出口）、耦合剩余 0.430 K。");
        W("照同一份输出文件的旋钮终值表复原设计、**单独跑 ②**：4.922 K（位置 HC1|HC2）、耦合剩余 0.661 K。");
        W("几何决定的量逐位相同、热的量差半度 —— 病因有两个互斥的可能：**跨解残留状态** 或 **复原不全**。");
        W("");
        W("═══════ 本轮跑什么（同一份设计、同一个进程、同一份代码；变的只有「跑 ② 之前做过什么」）═══════");
        W("(a) 单独跑 ②　　　—— 进程里第一次场解");
        W("(b) 先跑 ① 再跑 ②　—— 端到端那一跑的次序");
        W("(c) 先跑 ② 再跑 ②　—— 只量「跑过一次 ② 之后」");
        W("(d) 收尾再跑一次 ② —— 量整趟有没有漂");
        W("每一次都从**同一份设计的独立副本**起（Clone），四次之间不共用对象。");
        W("");
        W("── 判读（**跑前写死在代码里，跑完不挪**）");
        W("① 四份结果的**逐位签名**（每条判据的 值／限值／过不过／位置 + 铂重 + 耦合剩余 + 网格单元）全等");
        W("　 ⇒ 没有跨解残留状态（逐位 = 逐位，不设容差）；有一份不等 ⇒ 就是残留状态，点名哪一步之后变的。");
        W("② 复原自证：(a) 对端到端那一跑印的数 —— 最热铂高出热偶读数 4.529 K／管孔净流入 0.656 W／");
        W("　 管根低于热偶读数 4.511 K（位置 出口）／耦合剩余 0.430 K／铂重 4245 g。比不上 ⇒ 复原不全。");
        W("两条判读互相独立，不许互相替代。");
        W("");
        W("═══════ 输入 ═══════");
        W($"设计：照表复原，出处 {R48LW08NavDesign.Source}");
        var seed = R48LW08NavDesign.Build();
        W($"管：内径 {seed.TubeIdMm:0.#} mm，壁厚 {seed.WallMm:0.00} mm，管保温 {seed.TubeInsulMm:0.#} mm，"
          + $"段长 {string.Join("/", seed.SegLengthMm.Select(v => v.ToString("0")))} mm，设定 {string.Join("/", seed.SetpointC.Select(v => v.ToString("0")))} °C");
        W($"法兰：圆盘半径 {seed.DiscRadiusMm:0.#} mm，舌长 {seed.TabLengthMm:0.#} mm，舌半宽 {seed.TabHalfWidthMm:0.#} mm，"
          + $"舌根圆角 {seed.TabFilletMm:0.#} mm，环宽 {seed.RingWidthMm:0.#} mm，管孔半径 {seed.HoleRadiusMm:0.###} mm");
        W("旋钮终值（复原后当场印，与出处那张表对照）：");
        W("片\t板厚 mm\t舌保温 mm\t环倍率 t₁\t外级 t₂\t内级 r₁ mm\t外级 r₂ mm\t舌片厚 mm\t槽张角 °\t舌孔 R mm\t孔拉长比\t槽心 °\t孔心 x mm\t盘槽当地电流 °");
        foreach (string row in KnobRows(seed)) W(row);
        W($"网格：不指定 ⇒ 整线算例缺省（导航）。设计输入表：默认（DesignInputs 默认构造），服役 {p.DesignLifeHours} h。");
        W($"进度活页（临时，非交付物）：{live}");
        W("");

        // ══════════════════════════════════════════════════════════════════
        //  四次实跑
        // ══════════════════════════════════════════════════════════════════
        var runs = new List<(string Tag, LineResult R, double Sec)>();

        W("═══════ (a) 单独跑 ②（进程里第一次场解）═══════");
        runs.Add(Glass(W, "a 单独 ②", seed, p, probe));

        W("═══════ (b) 先跑 ① 再跑 ② ═══════");
        var rampSw = Stopwatch.StartNew();
        probe.Report("── (b) 先跑 ① 升温全程");
        var ramp = RampSweep.Run(seed.Clone(), p, new RampSweepOptions { RunClampAlt = false }, probe);
        rampSw.Stop();
        probe.Report($"── (b) ① 结束：{ramp.Verdict}，耗时 {rampSw.Elapsed.TotalSeconds:0} s");
        W($"① 升温全程：{ramp.Verdict}，耗时 {rampSw.Elapsed.TotalSeconds:0} s，{ramp.Points.Length} 个设定点"
          + $"（场有效 {ramp.Points.Count(q => q.FieldValid)}/{ramp.Points.Length}）。");
        runs.Add(Glass(W, "b ① 之后的 ②", seed, p, probe));

        W("═══════ (c) 先跑 ② 再跑 ② ═══════");
        probe.Report("── (c) 先垫一次 ②");
        var warm = SafeRun(seed.Clone().BuildCase(p), probe);
        W($"垫的那一次 ②：{(warm.Ok ? "解出来了" : "**没解出来**（" + warm.Message + "）")}，铂重 {warm.TotalMassG:0} g（只作垫场，不进对照）。");
        runs.Add(Glass(W, "c ② 之后的 ②", seed, p, probe));

        W("═══════ (d) 收尾再跑一次 ② ═══════");
        runs.Add(Glass(W, "d 收尾的 ②", seed, p, probe));

        // ══════════════════════════════════════════════════════════════════
        //  判读 ①：四份逐位自比
        // ══════════════════════════════════════════════════════════════════
        W("═══════ 判读 ①：四份结果逐位自比（跨解残留状态有没有）═══════");
        var sigs = runs.Select(q => Signature(q.R)).ToArray();
        W("对照基准 = (a)。「逐位」比的是每条判据的 值／限值／过不过／判不了／位置，再加铂重、管重、法兰重、耦合收敛、剩余误差、网格单元、整线解那句话。");
        W("次序\t与 (a) 比\t不同的地方");
        var diffs = new List<string>();
        for (int i = 0; i < runs.Count; i++)
        {
            var d = DiffLines(sigs[0], sigs[i]);
            if (i > 0 && d.Count > 0) diffs.Add($"({runs[i].Tag}) 与 (a) 不同 {d.Count} 处");
            W($"{runs[i].Tag}\t{(i == 0 ? "（基准）" : d.Count == 0 ? "**逐位相同**" : $"**有 {d.Count} 处不同**")}\t"
              + (d.Count == 0 ? "—" : string.Join("　｜　", d.Take(12)) + (d.Count > 12 ? $"　…（共 {d.Count} 处）" : "")));
        }
        W("");
        bool residual = diffs.Count > 0;
        W(residual
          ? "⇒ **有跨解残留状态**：同一份设计、同一份代码，只因为「跑 ② 之前做过什么」不同，结果就变了。" + string.Join("；", diffs)
          : "⇒ **没有跨解残留状态**：四种次序下 ② 的结果逐位相同 ⇒ 每次解确实是从干净状态起的。"
            + "　那么半度差只可能出在**复原设计**那一步（见判读 ②）。");
        W("");

        // ══════════════════════════════════════════════════════════════════
        //  判读 ②：复原自证
        // ══════════════════════════════════════════════════════════════════
        W("═══════ 判读 ②：复原自证（(a) 对端到端那一跑印的数）═══════");
        var a = runs[0].R;
        W("量\t端到端那一跑（出处见上）\t本轮 (a) 复原后单独跑 ②\t差\t对得上？");
        W(Cmp("最热铂高出热偶读数 K", R48LW08NavDesign.NavHotOverTcK, Val(a, LineResult.Key.HotOverTc)));
        W(Cmp("管孔净流入 W", R48LW08NavDesign.NavNetFluxW, Val(a, LineResult.Key.NetFlux)));
        W(Cmp("管根低于热偶读数 K", R48LW08NavDesign.NavColdUnderTcK, Val(a, LineResult.Key.ColdUnderTc)));
        W(Cmp("耦合剩余误差估计 K", R48LW08NavDesign.NavCoupleRemainK, a.CoupleRemainK));
        W(Cmp("铂重 g", R48LW08NavDesign.NavTotalMassG, a.TotalMassG));
        string wCold = Where(a, LineResult.Key.ColdUnderTc);
        W($"管根低于热偶读数的**位置**\t{R48LW08NavDesign.NavColdWhere}\t{wCold}\t—\t"
          + (wCold == R48LW08NavDesign.NavColdWhere ? "同" : "**不同**"));
        W("");
        W("  本轮 (a) 的交付判据逐条：");
        foreach (var c in a.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target)) W("    " + Line(c));
        W("");

        bool restoreOk = Same(R48LW08NavDesign.NavColdUnderTcK, Val(a, LineResult.Key.ColdUnderTc))
                      && Same(R48LW08NavDesign.NavHotOverTcK, Val(a, LineResult.Key.HotOverTc))
                      && Same(R48LW08NavDesign.NavNetFluxW, Val(a, LineResult.Key.NetFlux))
                      && wCold == R48LW08NavDesign.NavColdWhere;
        W(restoreOk
          ? "⇒ 复原出来的设计与端到端那一跑**对得上**（三条热判据与位置都同）。"
          : "⇒ 复原出来的设计与端到端那一跑**对不上** —— 照那张表复原出来的不是同一份设计（表里有字段没印）。");
        W("");

        W("═══════ 一句话 ═══════");
        W(residual
          ? "半度差的病因：**跨解残留状态**（同一设计、只换跑 ② 之前做过什么，结果就变）。"
          : restoreOk
            ? "半度差在本轮**没有复现**：四种次序逐位相同，且复原后单独跑 ② 与端到端那一跑对得上。"
              + "⚠ 这与 §0.-6 记的症状不符，须先查 §0.-6 那一跑的复原是怎么做的，才能收掉这条开放项。"
            : "半度差的病因：**复原设计那一步**。四种次序下 ② 的结果逐位相同（没有残留状态），"
              + "而照旋钮终值表复原出来的设计与端到端那一跑对不上 ⇒ 那张表不足以复原设计，缺的字段要补进端到端输出。");

        Finish(sb, file, totalSw);

        // ── 最低限度断言：每一步都要有结果（判据不过是结果，不是测试失败）
        Assert.All(runs, q => Assert.True(q.R.Ok, $"{q.Tag} 的整线解没解出来：{q.R.Message}"));
        Assert.Equal(4, runs.Count);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  帮手
    // ══════════════════════════════════════════════════════════════════════

    private static (string, LineResult, double) Glass(Action<string> W, string tag, DesignSpec seed, DesignInputs p, LiveProbe probe)
    {
        var sw = Stopwatch.StartNew();
        probe.Report($"── 开始 ② 带玻璃稳态（{tag}）");
        var r = SafeRun(seed.Clone().BuildCase(p), probe);
        sw.Stop();
        probe.Report($"── ② 结束（{tag}），耗时 {sw.Elapsed.TotalSeconds:0} s");
        W($"② 带玻璃稳态（{tag}）：{Verdict(r)}");
        W($"　耗时 {sw.Elapsed.TotalSeconds:0} s，网格单元 {r.MeshCells}，铂重 {r.TotalMassG:0} g，耦合收敛 {r.Converged}，剩余误差估计 {r.CoupleRemainK:0.000} K。");
        W($"　管根低于热偶读数 {Fmt(Val(r, LineResult.Key.ColdUnderTc))} K（位置 {Where(r, LineResult.Key.ColdUnderTc)}）"
          + $"／最热铂高出热偶读数 {Fmt(Val(r, LineResult.Key.HotOverTc))} K（位置 {Where(r, LineResult.Key.HotOverTc)}）"
          + $"／管孔净流入 {Fmt(Val(r, LineResult.Key.NetFlux))} W（位置 {Where(r, LineResult.Key.NetFlux)}）");
        W("");
        return (tag, r, sw.Elapsed.TotalSeconds);
    }

    /// <summary>逐位签名：把一次整线解印成一串「键 = 值」行，比对时逐行比。</summary>
    private static List<string> Signature(LineResult r)
    {
        var s = new List<string>
        {
            $"整线解.Ok = {r.Ok}",
            $"整线解.Message = {r.Message}",
            $"耦合收敛 = {r.Converged}",
            $"剩余误差估计 = {r.CoupleRemainK.ToString("R")}",
            $"网格单元 = {r.MeshCells}",
            $"铂重合计 = {r.TotalMassG.ToString("R")}",
            $"铂重管 = {r.TubeMassG.ToString("R")}",
            $"铂重法兰 = {r.FlangeMassG.ToString("R")}",
        };
        foreach (var c in r.Checks)
            s.Add($"判据[{c.Name}] 值 = {c.Actual.ToString("R")}　限值 = {c.Limit.ToString("R")}　过 = {c.Ok}"
                + $"　判不了 = {c.Undetermined}　暂不给数 = {c.Withheld}　位置 = {c.Where}");
        return s;
    }

    private static List<string> DiffLines(List<string> a, List<string> b)
    {
        var d = new List<string>();
        int n = Math.Max(a.Count, b.Count);
        for (int i = 0; i < n; i++)
        {
            string x = i < a.Count ? a[i] : "（缺）";
            string y = i < b.Count ? b[i] : "（缺）";
            if (!string.Equals(x, y, StringComparison.Ordinal)) d.Add($"{x} → {y}");
        }
        return d;
    }

    private static bool Same(double a, double b) => !double.IsNaN(a) && !double.IsNaN(b) && Math.Abs(a - b) <= PrintedTol;

    private static string Cmp(string name, double target, double got)
        => $"{name}\t{target:0.###}\t{Fmt(got)}\t{(double.IsNaN(got) ? "—" : (got - target).ToString("+0.###;-0.###"))}\t"
         + (Same(target, got) ? "同（印出来那几位上）" : "**不同**");

    private static double Val(LineResult r, string key)
        => r.Checks.FirstOrDefault(c => c.Name.StartsWith(key, StringComparison.Ordinal))?.Actual ?? double.NaN;

    private static string Where(LineResult r, string key)
        => r.Checks.FirstOrDefault(c => c.Name.StartsWith(key, StringComparison.Ordinal))?.Where ?? "—";

    private static LineResult SafeRun(LineCase lc, IProgress<string> probe)
    {
        try { return LineRunner.Run(lc, probe); }
        catch (Exception ex) { return new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
    }

    private static string Verdict(LineResult r)
        => !r.Ok ? $"整线解没解出来 ⇒ 判不了（{r.Message}）"
         : !r.Converged ? $"外层耦合未收敛（剩余误差估计 {r.CoupleRemainK:0.000} K）—— 场无效，判不了"
         : r.AllOk ? "全判据通过"
         : $"有判据不过或判不了：{string.Join("；", r.Failed)}";

    private static string Line(ConstraintOut c)
    {
        string kind = c.Kind switch
        {
            CheckKind.HardSafety => "卡交付（硬安全线）",
            CheckKind.Target => "卡交付（目标）",
            _ => "只作参考",
        };
        double slack = double.IsNaN(c.Actual) ? double.NaN : c.LessIsBetter ? c.Limit - c.Actual : c.Actual - c.Limit;
        return $"{Criteria.Plain(c.Name)}：实际 {(c.Withheld ? "暂不给数" : c.Actual.ToString("0.###"))} / 限值 {c.Limit:0.###} {c.Unit}，"
             + $"{(c.Undetermined ? "**无法判定**" : c.Ok ? "过" : "**不过**")}，裕度 {slack:+0.###;-0.###}，位置 {c.Where}　[{kind}]";
    }

    private static IEnumerable<string> KnobRows(DesignSpec s)
    {
        for (int j = 0; j < s.TabThickMm.Length; j++)
        {
            var rr = s.RingRadiiOf(j);
            yield return $"{j}\t{s.TabThickMm[j]:0.00}\t{s.TabInsulMm[j]:0.0}\t{s.RingMul[j]:0.00}\t{s.RingMulOuter(j):0.00}\t"
                       + $"{rr[0]:0.0}\t{rr[1]:0.0}\t"
                       + $"{(double.IsNaN(s.TongueThickMm[j]) ? "同基板" : s.TongueThickMm[j].ToString("0.00"))}\t"
                       + $"{s.SlotSpanDeg[j]:0.#}\t{s.TabHoleRMm[j]:0.0}\t{s.TabHoleAspect[j]:0.00}\t"
                       + $"{Nan(s.SlotCenterDeg[j])}\t{Nan(s.TabHoleXMm[j])}\t{Nan(s.DiscCutRotDeg[j])}";
        }
    }

    private static string Nan(double v) => double.IsNaN(v) ? "NaN" : v.ToString("0.###");
    private static string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("0.###");

    private void Finish(StringBuilder sb, string file, Stopwatch totalSw)
    {
        totalSw.Stop();
        sb.AppendLine();
        sb.AppendLine($"── 总耗时 {totalSw.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        sb.AppendLine("出处：带玻璃稳态 = LineRunner.Run；升温全程 = RampSweep.Run；设计 = R48LW08NavDesign.Build（照表复原，出处见上）。");
        sb.AppendLine("四次 ② 来自同一次运行、同一个进程，不拼两份输出。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(sb.ToString());
        Console.WriteLine(sb.ToString());
    }

    /// <summary>进度活页：每行带时刻与自上一行的耗时，当场落盘（长跑要放探针）。</summary>
    internal sealed class LiveProbe : IProgress<string>
    {
        private readonly string _path;
        private readonly object _lock = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private double _last;
        public LiveProbe(string path) { _path = path; File.WriteAllText(path, $"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n"); }
        public void Report(string value)
        {
            lock (_lock)
            {
                double now = _sw.Elapsed.TotalSeconds;
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss} [+{now - _last,7:0.0}s 累计{now / 60,7:0.0}min]  {value}\r\n");
                _last = now;
            }
        }
    }
}
