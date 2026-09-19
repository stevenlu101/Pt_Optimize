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
//  R48 L 路：**那半度差到底是「表里少印了字段」还是「这个数本身不可复现」**　2026-09-17，Opus 5。
//
//  ══ 前两步实测（同日，两份带开跑时刻的输出文件）
//  ① R48LSolveOrderTests：同一份设计、四种跑法（单独 ②／① 之后 ②／② 之后 ②／收尾 ②）
//     结果**逐位相同** ⇒ **没有跨解残留状态**。
//  ② R48LThreeMeshTests 第 2 步：把 A 导航档重新解一遍，拿求解器返回的设计对象与照表复原的那份
//     **按反射逐字段比**（DesignSpec 63 个公开实例字段）。进几何的只差三处：
//        TabInsulMm     [5.1000000000000005, 2.3000000000000003, 3.6, 10.4] ←→ [5.1, 2.3, 3.6, 10.4]
//        TongueThickMm  [2.0300000000000002, 3.5100000000000002, …]        ←→ [2.03, 3.51, …]
//        RingMul2       [1, 1, 1, 1]                                        ←→ [NaN, NaN, NaN, NaN]
//     前两处是**同一个十进制数的最后一位**（求解器落图纸格用 Ceiling(v/q)*q 算出来的那个 double，
//     与源码里写 5.1 这个字面量，不是同一个 double；相对差 ~1e-16）。
//     第三处是 **NaN = 走默认规则 1+0.4(t₁−1)**，t₁ = 1 时规则值就是 1 ⇒ 值相同。
//     而两份设计的「管根低于热偶读数」差 **0.411 K**（4.511 出口 ↔ 4.922 HC1|HC2），
//     耦合剩余误差估计差 0.231 K，铂重逐位相同。
//
//  ⇒ **不能**就此写成「那张表少印了三个字段」—— 那会把一件更重的事说轻：
//    如果 1 ulp 的输入差就能把这条硬安全线挪 0.4 K（它的裕度只有 0.489），
//    那么这条判据的**数本身不可复现**，补字段补不了。
//  本测试就是把这两种说法分开：**逐项单改，看哪一项、改多小就够**。
//
//  ══ 判读（**跑前写死在代码里，跑完不挪**）
//    · 重建（三项全改）与出处印的 4.511 K／位置 出口／耦合剩余 0.430 K 逐位相同
//      ⇒ 重建成立，差因确实落在这三项之内；对不上 ⇒ 差因还在三项之外，本测试不下结论。
//    · 逐项单改：只要有**一项本身只是最后一位之差**（TabInsulMm 或 TongueThickMm）就足以把
//      4.922 挪到 4.511 ⇒ 结论写「这条判据的值对输入的最后一位敏感，数不可复现」，
//      **不许**写成「表里少印了字段」。
//    · 只在 RingMul2（NaN ↔ 1）那一项上变 ⇒ 先验两者的规则值是否相同；相同则同上。
//    · 最小扰动：拿**求解器那份几何**只把 TabInsulMm[0] 加一个 ulp（BitIncrement），
//      看这条判据动多少 —— 这是能造出来的最小输入差。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48LColdUnderTcConditioningTests
{
    private readonly ITestOutputHelper _o;
    public R48LColdUnderTcConditioningTests(ITestOutputHelper o) { _o = o; }

    // ── 求解器那份设计的三处值（出自 R48LThreeMeshTests 的反射逐字段比，R 格式往返，逐位）
    private static double[] SolverTabInsul() => new[] { 5.1000000000000005, 2.3000000000000003, 3.6, 10.4 };
    private static double[] SolverTongue() => new[] { 2.0300000000000002, 3.5100000000000002, 3.5100000000000002, 2.0300000000000002 };
    private static double[] SolverRingMul2() => new[] { 1.0, 1.0, 1.0, 1.0 };

    /// <summary>出处那一跑（也就是本轮重解那一跑）印的靶子。</summary>
    private const double TargetColdK = 4.511;
    private const string TargetColdWhere = "出口";
    private const double TargetRemainK = 0.430;

    [Fact]
    public void 管根低于热偶读数_对输入最后一位的敏感度()
    {
        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_管根判据_最后一位敏感度_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        string live = Path.Combine(Path.GetTempPath(), $"R48_L_敏感度_{stamp}_进行中.log");
        var probe = new R48LSolveOrderTests.LiveProbe(live);

        W("R48 L 路　**那半度差：是「表里少印了字段」还是「这个数本身不可复现」**");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("");
        W("═══════ 前两步实测（同日，两份带开跑时刻的输出文件）═══════");
        W("① R48LSolveOrderTests：同一份设计、四种跑法结果**逐位相同** ⇒ 没有跨解残留状态。");
        W("② R48LThreeMeshTests 第 2 步：重新解一遍 A 导航档，反射逐字段比（63 个公开实例字段），进几何的只差三处：");
        W("　　TabInsulMm　　[5.1000000000000005, 2.3000000000000003, 3.6, 10.4] ←→ [5.1, 2.3, 3.6, 10.4]");
        W("　　TongueThickMm [2.0300000000000002, 3.5100000000000002, …] ←→ [2.03, 3.51, …]");
        W("　　RingMul2　　　[1, 1, 1, 1] ←→ [NaN, NaN, NaN, NaN]（NaN = 走默认规则 1+0.4(t₁−1)）");
        W("　而两份设计的「管根低于热偶读数」差 **0.411 K**（4.511 出口 ↔ 4.922 HC1|HC2），铂重逐位相同。");
        W("");
        W("═══════ 本轮跑什么（同一个进程、同一份代码、同一张网格；变的只有那三项里的一项）═══════");
        W("底本 = 照表复原的那份设计（R48LW08NavDesign.Build）。逐项单改，每改一项跑一次 ② 带玻璃稳态。");
        W("");
        W("── 判读（**跑前写死在代码里，跑完不挪**）");
        W($"· 重建（三项全改）与出处印的 {TargetColdK:0.###} K／位置 {TargetColdWhere}／耦合剩余 {TargetRemainK:0.###} K 逐位相同");
        W("　⇒ 重建成立，差因确实落在这三项之内；对不上 ⇒ 差因还在三项之外，本测试不下结论。");
        W("· 只要有**一项本身只是最后一位之差**就足以把 4.922 挪到 4.511 ⇒ 结论写「这条判据的值对输入的最后一位敏感、");
        W("　数不可复现」，**不许**写成「表里少印了字段」。");
        W("· 最小扰动：拿求解器那份几何只把 TabInsulMm[0] 加一个 ulp，看这条判据动多少。");
        W("");

        var baseD = R48LW08NavDesign.Build();
        W("═══════ 三处差的「差有多大」（先量，再谈因果）═══════");
        W("项\t求解器的值\t复原的值\t绝对差\t相对差\t相差几个 ulp");
        for (int j = 0; j < 4; j++) W(UlpRow($"TabInsulMm[{j}]", SolverTabInsul()[j], baseD.TabInsulMm[j]));
        for (int j = 0; j < 4; j++) W(UlpRow($"TongueThickMm[{j}]", SolverTongue()[j], baseD.TongueThickMm[j]));
        var rm2 = baseD.Clone(); rm2.RingMul2 = SolverRingMul2();
        W($"RingMul2 的**规则值**（RingMulOuter）\t{baseD.RingMulOuter(0):R}（NaN ⇒ 走规则）\t{rm2.RingMulOuter(0):R}（写死 1）\t"
          + $"{Math.Abs(baseD.RingMulOuter(0) - rm2.RingMulOuter(0)):R}\t—\t"
          + (baseD.RingMulOuter(0) == rm2.RingMulOuter(0) ? "**值相同**（这一项不是几何差）" : "**值不同**"));
        W("");

        // ── 六次实跑（六份设计的造法是**全仓唯一一份** Variants()，R48LCoupleTolTightenTests 调同一份 —— 不许各抄一遍）
        var runs = new List<(string Tag, LineResult R)>();
        var v6ref = Variants()[5].D;
        foreach (var v in Variants()) runs.Add(Run(W, v.Tag, v.D, probe, p));

        // ── 并列
        W("═══════ 并列（同一张网格 2.000 mm／半径 50.0，只换那一项）═══════");
        W("跑法\t管根低于热偶读数 K\t位置\t最热铂高出热偶读数 K\t管孔净流入 W\t耦合剩余 K\t铂重 g");
        foreach (var q in runs)
            W($"{q.Tag}\t{Fmt(Val(q.R, LineResult.Key.ColdUnderTc))}\t{Where(q.R, LineResult.Key.ColdUnderTc)}\t"
              + $"{Fmt(Val(q.R, LineResult.Key.HotOverTc))}\t{Fmt(Val(q.R, LineResult.Key.NetFlux))}\t"
              + $"{q.R.CoupleRemainK:0.000}\t{q.R.TotalMassG:0.###}");
        W("");
        W("── 逐接头的「管根低于热偶读数」（判据取最坏的那个接头 ⇒ 两个接头挨得近时，谁最坏会翻面）");
        foreach (var q in runs)
            W($"{q.Tag}：{PerJoint(q.R)}");
        W("");

        // ── 判读
        var r5 = runs[4].R;
        bool rebuilt = Math.Abs(Val(r5, LineResult.Key.ColdUnderTc) - TargetColdK) <= 5e-4
                    && Where(r5, LineResult.Key.ColdUnderTc) == TargetColdWhere
                    && Math.Abs(r5.CoupleRemainK - TargetRemainK) <= 5e-4;
        W("═══════ 判读（按跑前写死的那几条）═══════");
        W($"重建（⑤）：管根 {Fmt(Val(r5, LineResult.Key.ColdUnderTc))} K（位置 {Where(r5, LineResult.Key.ColdUnderTc)}）、耦合剩余 {r5.CoupleRemainK:0.000} K　"
          + $"对靶子 {TargetColdK:0.###}／{TargetColdWhere}／{TargetRemainK:0.###} ⇒ {(rebuilt ? "**对得上**" : "**对不上**")}");
        if (!rebuilt)
            W("⇒ 重建对不上 ⇒ 差因还在那三项之外，本测试**不下结论**，把并列表原样交出去。");
        else
        {
            double c0 = Val(runs[0].R, LineResult.Key.ColdUnderTc);
            var movers = new List<string>();
            for (int i = 1; i <= 3; i++)
                if (Math.Abs(Val(runs[i].R, LineResult.Key.ColdUnderTc) - c0) > 5e-4) movers.Add(runs[i].Tag);
            W($"单改就能把它挪动的项：{(movers.Count == 0 ? "**一项都没有**（只有合起来改才动）" : string.Join("；", movers))}");
            double d6 = Val(runs[5].R, LineResult.Key.ColdUnderTc) - Val(r5, LineResult.Key.ColdUnderTc);
            W($"最小扰动（⑥：只把 舌保温[片0] 加一个 ulp，相对差 ~{Math.Abs(SolverTabInsul()[0] - v6ref.TabInsulMm[0]) / SolverTabInsul()[0]:0.0E+0}）"
              + $"⇒ 管根动了 **{d6:+0.###;-0.###} K**（位置 {Where(runs[5].R, LineResult.Key.ColdUnderTc)}）");
            W("");
            bool ulpSensitive = Math.Abs(d6) > 5e-4
                             || movers.Any(m => m.Contains("舌保温") || m.Contains("舌片厚"));
            W(ulpSensitive
              ? "⇒ **结论：这条判据的值对输入的最后一位敏感 —— 这个数不可复现。**\n"
                + "　半度差**不是**「端到端那张表少印了字段」：表里的三项，两项只差一个十进制数的最后一位\n"
                + "　（求解器落图纸格算出来的 double 与源码字面量不是同一个 double），第三项值相同。\n"
                + "　补字段补不了它。要让这个数可复现，得先让它**对输入连续**：\n"
                + "　· 现象：判据取「最坏的那个接头」，而两个接头挨得很近（见上面逐接头那一行），\n"
                + "　　 外层耦合停在不同的迭代上就会换一个接头当最坏 ⇒ 值跳、位置跳。\n"
                + "　· 这与耦合剩余误差估计（0.430 ↔ 0.661 K）同量级 ⇒ **判据的跳动量与耦合停机噪声分不开**，\n"
                + "　　 而它的裕度只有 0.489 K —— 也就是说这条硬安全线的裕度**整个落在噪声里**。"
              : "⇒ 单个最后一位改不动它；差是三项合起来才出来的。按实测写，不往「不可复现」那一支靠。");
        }
        W("");
        W("═══════ 仍开放 ═══════");
        W("· 怎么让它可复现：收紧外层耦合的停机判据、或把「取最坏接头」改成对停机噪声不敏感的取法 —— 本轮没动生产。");
        W("· 在它可复现之前，**任何一份报告里这条判据的数都只能带着耦合剩余误差估计一起读**；");
        W("　裕度小于剩余误差估计时，「过」与「不过」分不开。");

        Finish(sb, file, totalSw);

        Assert.Equal(6, runs.Count);
        Assert.All(runs, q => Assert.True(q.R.Ok, $"{q.Tag} 的整线解没解出来：{q.R.Message}"));
    }

    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ R48 L（2026-09-17，Opus 5）：**这六份设计的造法，全仓只有这一份**（本测试与 R48LCoupleTolTightenTests 共用）。
    /// 收紧停机容差之后要拿同一组扰动重跑一遍比摆幅 —— 两处各抄一份就会比到两组不同的设计上去，
    /// 而那种错**看起来完全正常**（六个标签一模一样）。
    /// </summary>
    internal static (string Tag, DesignSpec D)[] Variants()
    {
        var baseD = R48LW08NavDesign.Build();

        var v2 = baseD.Clone(); v2.TabInsulMm = SolverTabInsul();
        var v3 = baseD.Clone(); v3.TongueThickMm = SolverTongue();
        var v4 = baseD.Clone(); v4.RingMul2 = SolverRingMul2();
        var v5 = baseD.Clone();
        v5.TabInsulMm = SolverTabInsul(); v5.TongueThickMm = SolverTongue(); v5.RingMul2 = SolverRingMul2();
        var v6 = v5.Clone();
        v6.TabInsulMm = (double[])SolverTabInsul().Clone();
        v6.TabInsulMm[0] = Math.BitIncrement(v6.TabInsulMm[0]);   // 能造出来的最小输入差：一个 ulp

        return new[]
        {
            ("① 底本：照表复原（全部用源码字面量）", baseD),
            ("② 只把 舌保温 换成求解器那份的最后一位", v2),
            ("③ 只把 舌片厚 换成求解器那份的最后一位", v3),
            ("④ 只把 外级倍率 从 NaN（走规则）换成写死的 1", v4),
            ("⑤ 三项全改 = 求解器那份几何（重建）", v5),
            ("⑥ 在 ⑤ 的基础上，只把 舌保温[片0] 加一个 ulp", v6),
        };
    }

    /// <summary>判据值的读口（本测试与 R48LCoupleTolTightenTests 共用，不各写一遍 StartsWith 匹配）。</summary>
    internal static double ValueOf(LineResult r, string key) => Val(r, key);
    /// <summary>判据位置的读口（同上）。</summary>
    internal static string WhereOf(LineResult r, string key) => Where(r, key);
    /// <summary>判据 Note 里「逐片」那一段的读口（同上）。</summary>
    internal static string PerJointOf(LineResult r) => PerJoint(r);

    private static (string, LineResult) Run(Action<string> W, string tag, DesignSpec d,
                                            R48LSolveOrderTests.LiveProbe probe, DesignInputs p)
    {
        var sw = Stopwatch.StartNew();
        probe.Report($"── 开始 ②（{tag}）");
        LineResult r;
        try { r = LineRunner.Run(d.Clone().BuildCase(p), probe); }
        catch (Exception ex) { r = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
        sw.Stop();
        probe.Report($"── ② 结束（{tag}），耗时 {sw.Elapsed.TotalSeconds:0} s");
        W($"── {tag}");
        W($"　管根低于热偶读数 {Fmt(Val(r, LineResult.Key.ColdUnderTc))} K（位置 {Where(r, LineResult.Key.ColdUnderTc)}）"
          + $"／最热铂 {Fmt(Val(r, LineResult.Key.HotOverTc))} K／净流入 {Fmt(Val(r, LineResult.Key.NetFlux))} W"
          + $"／耦合剩余 {r.CoupleRemainK:0.000} K／铂重 {r.TotalMassG:0.###} g／耗时 {sw.Elapsed.TotalSeconds:0} s"
          + $"／{(r.AllOk ? "全判据通过" : "有判据不过或判不了")}");
        W("");
        return (tag, r);
    }

    private static string UlpRow(string name, double a, double b)
    {
        double diff = Math.Abs(a - b);
        long ulps = Math.Abs(BitConverter.DoubleToInt64Bits(a) - BitConverter.DoubleToInt64Bits(b));
        return $"{name}\t{a:R}\t{b:R}\t{diff:R}\t{(b != 0 ? (diff / Math.Abs(b)).ToString("0.0E+0") : "—")}\t{ulps}";
    }

    private static ConstraintOut? Get(LineResult r, string key)
        => r.Checks.FirstOrDefault(c => c.Name.StartsWith(key, StringComparison.Ordinal));

    private static double Val(LineResult r, string key) => Get(r, key)?.Actual ?? double.NaN;
    private static string Where(LineResult r, string key) => Get(r, key)?.Where ?? "—";

    /// <summary>从判据自己的 Note 里取「逐片」那一段 —— 不在门里另算一份逐接头的数。</summary>
    private static string PerJoint(LineResult r)
    {
        string note = Get(r, LineResult.Key.ColdUnderTc)?.Note ?? "";
        int i = note.IndexOf("逐片", StringComparison.Ordinal);
        if (i < 0) return note.Length > 0 ? note : "（判据没给逐片的数）";
        int j = note.IndexOf('。', i);
        return j > i ? note.Substring(i, j - i) : note.Substring(i);
    }

    private static string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("0.###");

    private void Finish(StringBuilder sb, string file, Stopwatch totalSw)
    {
        totalSw.Stop();
        sb.AppendLine();
        sb.AppendLine($"── 总耗时 {totalSw.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        sb.AppendLine("出处：带玻璃稳态 = LineRunner.Run；底本设计 = R48LW08NavDesign.Build；"
                    + "三处差的值 = R48LThreeMeshTests 那一跑的反射逐字段比（R 格式往返，逐位）。");
        sb.AppendLine("六次来自同一次运行、同一个进程，不拼两份输出。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(sb.ToString());
        Console.WriteLine(sb.ToString());
    }
}
