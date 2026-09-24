using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★ R48 I 路（2026-09-15，Opus 5；合并把关待办 P0-4）：**保温搜索每层的板厚是约束盒下角，与传入板厚无关**。
///
/// 病：InsulationSearch 每层直接在传入设计的板厚上调 Solver.ApplySectionFloor（只增不减）⇒ deliverable/R48_保温搜索_小规模_2026-09-15.txt 第 15、20、26 行
///   三层管保温（7.5／8.0／8.5 mm）板厚都「传入 0.73/1.26/1.26/0.73 → 下角后」同值。
/// 实测（本文件「测量」一条，2026-09-15 Opus 5）：板厚先置求解器的约束盒下角（DiscFloorMm 0.6 落图纸格 = 0.60）再 ApplySectionFloor，三层分别是
///   0.65/1.12/1.12/0.65、0.63/1.09/1.09/0.63、0.62/1.07/1.07/0.62 ⇒ 0.73/1.26 **不是** J 下角，保温搜索继承了传入值（7.5 mm 下片0、共用片都比下角厚约 12 %，8.5 mm 下约 18 %）。
/// 修：InsulationSearch.LayerDesign —— 板厚逐片先置 Solver.ThickLowerCornerMm（从 Solver.Solve 起点那一行原样提出的公开函数，两处同一份）再 ApplySectionFloor。
///
/// 门（跑前写死）：
///   a 行为：三层管保温、三种传入板厚（0.73/1.26/1.26/0.73、全 2.0、全 0.60），LayerDesign 的逐片板厚与舌片厚三者逐位相同；
///     结果每片 ≥ ThickLowerCornerMm、在图纸格上；对结果再调一次 ApplySectionFloor 不再上抬（已在按 J 的下界上）。
///   b 源码：Solver.Solve 起点调 ThickLowerCornerMm；InsulationSearch.RunLayer 调 LayerDesign；两个 Core 文件里「DiscFloorMm(…) / … QuantThickMm」的向上落格式子只剩 ThickLowerCornerMm 那一处
///     （2026-09-16 Opus 5 改按方法体查、不认变量名，审查意见 2）。
///   c 行为（2026-09-16 Opus 5 加，审查意见 2「RunLayer 是否真走 LayerDesign 只有文本门守着」）：Run 预先取消、在第一轮不动点前截住，RunLayer 报告行里的板厚／舌片厚／下角与 LayerDesign 逐片相同。
///   门 a 另加四条与被测函数无关的断言（2026-09-16 Opus 5，审查意见 1）：下角 ≥ DiscFloorMm、高出不到一格、在图纸格上，W08 写死 0.60。
/// ⚠ 测量顺带暴露（不在本条修）：ApplySectionFloor「截面积正比于板厚 ⇒ 闭式一步」不精确（舌盘交界焊弧取舌片厚、不随基板变）——
///   从 0.60 一步抬到 1.12，而逐格量圆盘侧最紧截面 J：1.10 已 9.987 ≤ 10（1.09 为 10.08）⇒ 共用片的下角比按 J 的最小格高 1～2 格（7.5 mm 高 0.02、8.0／8.5 mm 高 0.01）。求解器同病。
/// </summary>
public class R48InsulationSearchPlateFloorTests
{
    private readonly ITestOutputHelper _out;
    public R48InsulationSearchPlateFloorTests(ITestOutputHelper o) { _out = o; }
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static DesignSpec Design(double[] thick)
    {
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = (double[])thick.Clone();
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        return d.Fit();
    }

    /// <summary>测量（只印）：甲 = 旧做法（沿用传入板厚再 ApplySectionFloor），乙 = LayerDesign；并逐格印圆盘侧最紧截面 J（同一个 SectionSizing.PlateThickFloorMm）。</summary>
    [Fact]
    public void 测量_三层管保温_沿用传入板厚对约束盒下角()
    {
        var p = new DesignInputs { SplitSharedFlangeDraw = true };
        var o = new SolverOptions();
        foreach (int layer in new[] { 15, 16, 17 })
        {
            var a = Design(new[] { 0.73, 1.26, 1.26, 0.73 }).Clone().Fit();
            a.TubeInsulMm = InsulationSearch.Mm(layer);
            var ra = new SolverResult();
            var (_, floorsA) = Solver.ApplySectionFloor(a, p, o, ra, null);
            var rb = new SolverResult();
            var b = InsulationSearch.LayerDesign(Design(new[] { 0.73, 1.26, 1.26, 0.73 }), p, layer, rb, null, out double tLo);
            _out.WriteLine($"管保温 {a.TubeInsulMm} mm：设计电流 片 {string.Join("/", rb.DesignCurrent!.PlateA.Select(v => v.ToString("0.0", Inv)))} A；约束盒下角 {tLo.ToString("R", Inv)}；"
                + $"甲（旧：沿用传入）板厚 {string.Join("/", a.TabThickMm.Select(v => v.ToString("R", Inv)))}、闭式 J 下界 {string.Join("/", floorsA.Select(v => v.ToString("R", Inv)))}；"
                + $"乙（LayerDesign）板厚 {string.Join("/", b.TabThickMm.Select(v => v.ToString("R", Inv)))}；"
                + $"舌片厚 甲 {string.Join("/", a.TongueThickMm.Select(v => v.ToString("R", Inv)))} 乙 {string.Join("/", b.TongueThickMm.Select(v => v.ToString("R", Inv)))}");
            for (int j = 0; j < b.TabThickMm.Length; j++)
            {
                double iA = rb.DesignCurrent!.PlateA[j];
                var cells = new List<string>();
                for (int k = -4; k <= 1; k++)
                {
                    var c = b.Clone();
                    double t = Math.Round(b.TabThickMm[j] + k * o.QuantThickMm, 10);
                    c.TabThickMm[j] = t;
                    var g = c.Plate(j, c.DiscFloorMm(p));
                    double jNow = SectionSizing.PlateThickFloorMm(g, t, iA, c.ClampLengthMm, c.JDesignAPerMm2) / t * c.JDesignAPerMm2;
                    cells.Add($"t {t.ToString("0.00", Inv)} → J {jNow.ToString("0.0000", Inv)}");
                }
                _out.WriteLine($"   片{j}（设计电流 {iA.ToString("0.0", Inv)} A，设定 J {b.JDesignAPerMm2}）：" + string.Join("；", cells));
            }
        }
    }

    [Fact]
    public void 门a_每层板厚与传入板厚无关_在按J的下界上()
    {
        var p = new DesignInputs { SplitSharedFlangeDraw = true };
        var o = new SolverOptions();
        var inputs = new[] { new[] { 0.73, 1.26, 1.26, 0.73 }, new[] { 2.0, 2.0, 2.0, 2.0 }, new[] { 0.60, 0.60, 0.60, 0.60 } };
        foreach (int layer in new[] { 15, 16, 17 })
        {
            var outs = inputs.Select(th => InsulationSearch.LayerDesign(Design(th), p, layer, new SolverResult(), null, out _)).ToArray();
            for (int k = 1; k < outs.Length; k++)
            {
                Assert.True(outs[0].TabThickMm.SequenceEqual(outs[k].TabThickMm),
                    $"管保温层 {layer}：传入板厚 {string.Join("/", inputs[k])} 得板厚 {string.Join("/", outs[k].TabThickMm)}，传入 {string.Join("/", inputs[0])} 得 {string.Join("/", outs[0].TabThickMm)} —— 结果随传入板厚变");
                Assert.True(outs[0].TongueThickMm.SequenceEqual(outs[k].TongueThickMm), $"管保温层 {layer}：舌片厚随传入板厚变");
            }
            var d = outs[0];
            double lo = Solver.ThickLowerCornerMm(d, p, o);
            // 2026-09-16 Opus 5（I 路审查意见 1）：ThickLowerCornerMm 是被测函数，不能只拿它当裁判 —— 下面四条与它的实现无关：
            //   下角 ≥ 焊接屈曲/烧穿下界 DiscFloorMm、只往上落不到一格、在图纸格上；对 W08 写死期望 0.60（DiscFloorMm = max(屈曲 0.55～0.58, 烧穿 0.6) = 0.6，向上落 0.01 格 = 0.60；
            //   出处 DesignSpec.DiscFloorMm 注释与 deliverable\R48_I_保温搜索板厚下角_测量与门_控制台_本次开跑于2026-09-15_1949.txt「约束盒下角 0.6」）。
            //   注入实验（2026-09-16）：ThickLowerCornerMm 的 Math.Ceiling 改 Math.Floor ⇒ 下角 0.59 < 0.60，此前整套快门仍绿（审查实测）；加了这四条后本门红。
            double discFloor = d.DiscFloorMm(p);
            Assert.True(lo >= discFloor - 1e-12, $"约束盒下角 {lo} 低于焊接屈曲/烧穿下界 DiscFloorMm {discFloor}（向下落格了？）");
            Assert.True(lo - discFloor < o.QuantThickMm, $"约束盒下角 {lo} 比 DiscFloorMm {discFloor} 高出不止一格 {o.QuantThickMm}");
            Assert.True(Math.Abs(lo / o.QuantThickMm - Math.Round(lo / o.QuantThickMm)) < 1e-6, $"约束盒下角 {lo} 不在图纸格 {o.QuantThickMm} 上");
            Assert.True(Math.Abs(lo - 0.60) < 1e-9, $"W08 的约束盒下角应为 0.60（烧穿下界 0.6 落格），实得 {lo}");
            foreach (double t in d.TabThickMm)
            {
                Assert.True(t >= lo - 1e-12, $"板厚 {t} 低于约束盒下角 {lo}");
                Assert.True(Math.Abs(t / o.QuantThickMm - Math.Round(t / o.QuantThickMm)) < 1e-6, $"板厚 {t} 不在图纸格 {o.QuantThickMm} 上");
            }
            var again = d.Clone();
            var (raised, _) = Solver.ApplySectionFloor(again, p, o, new SolverResult(), null);
            Assert.False(raised, $"管保温层 {layer}：对结果再按 J 抬一次还会上抬 —— 不在按 J 的下界上");
            Assert.True(again.TabThickMm.SequenceEqual(d.TabThickMm));
        }
    }

    /// <summary>
    /// 源码门。2026-09-16 Opus 5（I 路审查意见 2）：原来整行字面匹配（「var d = LayerDesign(d0, p, T, sres, floorLog.Add, out double thickLo);」），改个变量名就误红 ——
    /// 改为按方法体查：去掉注释后切出 RunLayer／LayerDesign 的方法体，只看「调了谁、没调谁」，不认变量名。
    /// </summary>
    [Fact]
    public void 门b_源码_下角只有一份_保温搜索调LayerDesign()
    {
        string core = Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core");
        static string Code(string f) => string.Join("\n", File.ReadAllText(f).Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith("///", StringComparison.Ordinal)));
        // 方法体：从签名开头到下一个方法签名（去缩进后的行首 public/private/internal static）
        static string Body(string code, string signatureStart)
        {
            int i = code.IndexOf(signatureStart, StringComparison.Ordinal);
            Assert.True(i >= 0, $"源码里找不到「{signatureStart}」");
            var next = Regex.Match(code.Substring(i + signatureStart.Length), @"\n(public|private|internal)\s+static\s");
            return next.Success ? code.Substring(i, signatureStart.Length + next.Index) : code.Substring(i);
        }
        string sol = Code(Path.Combine(core, "Solver.cs")), ins = Code(Path.Combine(core, "InsulationSearch.cs"));
        // ① 向上落格的式子全仓只剩 ThickLowerCornerMm 一份
        // ★ 2026-09-18 Opus 5（合并 I×L 改门，写明变因）：L 路把「向上对齐图纸格」提成了 Solver.SnapUpToGridMm（全仓唯一一份写法，门 R48LQuantInsul05Tests），
        //   ThickLowerCornerMm 的函数体随之由手抄的 Math.Ceiling 式子改成调它 ⇒ 本门原来数「除以 QuantThickMm 的式子恰好一处」现在是**零处**，门槛由 Single 改 Empty（更严，不是放宽），
        //   另加一条钉住 ThickLowerCornerMm 真的走那份唯一写法 —— 否则「零处」可以靠把式子挪走而不是接上来满足。
        Assert.Contains("public static double ThickLowerCornerMm(DesignSpec d, DesignInputs baseIn, SolverOptions opt)", sol);
        var floorQuant = new Regex(@"DiscFloorMm\([^)]*\)\s*/\s*\w+\.QuantThickMm");
        Assert.Empty(floorQuant.Matches(sol));
        Assert.Empty(floorQuant.Matches(ins));
        Assert.Contains("=> SnapUpToGridMm(d.DiscFloorMm(baseIn), opt.QuantThickMm);", sol);
        // ② Solver.Solve 的起点调它（定义那一处之外至少还有一次调用）
        Assert.True(Regex.Matches(sol, @"\bThickLowerCornerMm\s*\(").Count >= 2, "Solver.cs 里 ThickLowerCornerMm 只有定义、没有调用");
        // ③ LayerDesign 先置下角再按 J 抬
        string layerDesign = Body(ins, "public static DesignSpec LayerDesign(");
        Assert.Contains("Solver.ThickLowerCornerMm(", layerDesign);
        Assert.Contains("Solver.ApplySectionFloor(", layerDesign);
        Assert.True(layerDesign.IndexOf("Solver.ThickLowerCornerMm(", StringComparison.Ordinal) < layerDesign.IndexOf("Solver.ApplySectionFloor(", StringComparison.Ordinal), "LayerDesign 里 ApplySectionFloor 跑在置下角之前");
        // ④ RunLayer 只经 LayerDesign 定板厚：调 LayerDesign，自己不调 ApplySectionFloor／ThickLowerCornerMm
        string runLayer = Body(ins, "private static LayerResult RunLayer(");
        Assert.True(Regex.IsMatch(runLayer, @"\bLayerDesign\s*\("), "RunLayer 没有调 LayerDesign");
        Assert.DoesNotContain("ApplySectionFloor(", runLayer);
        Assert.DoesNotContain("ThickLowerCornerMm(", runLayer);
    }

    /// <summary>
    /// ★ 2026-09-16 Opus 5（I 路审查意见 2，造门在下游）：**保温搜索链路里 RunLayer 真用的板厚 = LayerDesign 的板厚**（行为门，不看源码）。
    /// 做法：InsulationSearch.Run 只算管保温 15 层（7.5 mm），取消令牌**预先取消**、两起点串行 ⇒ 链路走完 LayerDesign 与内层网格、在第一轮不动点之前
    ///   （FixedPoint 开头 ThrowIfCancellationRequested）抛 OperationCanceledException；此时 RunLayer 已经把本层报告行送进 Options.Log ——
    ///   「舌片厚（I/(J·舌宽)）… mm；板厚 传入 …（不用）→ 约束盒下角 … → 按 J 抬后 … mm」。从这一行解析出板厚、舌片厚、下角，与 LayerDesign(d0, p, 15, …) 逐片比（都按报告的 0.00 格式；板厚在 0.01 图纸格上，格式不丢位）。
    /// 另写死记录：7.5 mm 层板厚 0.65/1.12/1.12/0.65（出处 deliverable\R48_I_保温搜索板厚下角_测量与门_控制台_本次开跑于2026-09-15_1949.txt「乙（LayerDesign）板厚」）——
    ///   ApplySectionFloor 一步闭式修了（共用片 1.12 → 1.10，见类注释 ⚠）这里要重记，写旧值 → 新值与依据。
    /// 注入实验（2026-09-16，结果见 HANDOVER I 路修复注记）：RunLayer 换回旧就地写法（克隆 d0、换管保温、直接 ApplySectionFloor，不调 LayerDesign）⇒ 本门要红（报告板厚 0.73/1.26/1.26/0.73 ≠ LayerDesign 的 0.65/1.12/1.12/0.65）。
    /// ⚠ **这是字串门**（2026-09-16 Opus 5 按复核意见 4 补）：Run 抛 OperationCanceledException 之后 LayerResult 拿不到，快门只能读 RunLayer 已送进 Options.Log 的那一行 ——
    ///   断言落在**解析出来的报告字串**上（两位小数格式），不是链路里的双精度真值；**下游真值由慢探针 R48InsulationSearchProbeTests 的逐位断言守**（见下一句）。
    /// 不覆盖：不动点各轮、评估函数（那是慢探针 R48InsulationSearchProbeTests 的事，那里也加了 lr.PlateThickMm 逐位等于 LayerDesign 的断言）。
    /// </summary>
    [Fact]
    public void 门c_保温搜索链路_RunLayer报告的板厚与舌片厚等于LayerDesign()
    {
        // 决 103（2026-09-24）：有意改动 —— 保温搜索逐格点三项只有改前口径（InsulationSearch.RuleSetOfSearch），生产口径下 Run 拒答 ⇒ 本门在改回口径下验这条链路。
        var p = new DesignInputs { SplitSharedFlangeDraw = true, CriteriaRuleSet = InsulationSearch.RuleSetOfSearch };
        var d0 = Design(new[] { 0.73, 1.26, 1.26, 0.73 });
        const int layer = 15;
        var expect = InsulationSearch.LayerDesign(d0.Clone().Fit(), p, layer, new SolverResult(), null, out double loExpect);
        string Fmt(IEnumerable<double> v) => string.Join("/", v.Select(x => x.ToString("0.00")));   // 与 RunLayer 报告行同一格式（当前区域）
        string expectThick = Fmt(expect.TabThickMm), expectTongue = Fmt(expect.TongueThickMm), expectLo = loExpect.ToString("0.00");
        Assert.Equal("0.65/1.12/1.12/0.65", expectThick);   // 记录（2026-09-15 实测），ApplySectionFloor 改了要重记

        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        var lines = new List<string>();
        object lk = new();
        var o = new InsulationSearch.Options
        {
            TubeLayers = new[] { layer }, DiscLayerMax = 24, TabLayerMax = 24,
            LayersInParallel = false, StartsInParallel = false, MaxFixedPointRounds = 8,
            Log = s => { lock (lk) lines.Add(s); },
            Cancel = cts.Token,
        };
        var ex = Record.Exception(() => InsulationSearch.Run(d0, p, o));
        Assert.True(ex is OperationCanceledException, $"预先取消的令牌应在第一轮不动点前抛 OperationCanceledException，实得 {ex?.GetType().Name ?? "没抛"}：{ex?.Message}");
        string[] snapshot; lock (lk) snapshot = lines.ToArray();
        var m = snapshot.Select(l => Regex.Match(l, @"舌片厚（I/\(J·舌宽\)）([0-9./]+) mm；板厚 传入 ([0-9./]+)（不用）→ 约束盒下角 ([0-9.]+) → 按 J 抬后 ([0-9./]+) mm")).FirstOrDefault(x => x.Success);
        Assert.True(m is not null, "RunLayer 的板厚报告行没出现（取消得太早，或报告行格式改了）：\n" + string.Join("\n", snapshot));
        _out.WriteLine("RunLayer 报告行：" + m!.Value);
        Assert.Equal(Fmt(d0.TabThickMm), m.Groups[2].Value);     // 传入的确是 0.73/1.26/1.26/0.73
        Assert.Equal(expectLo, m.Groups[3].Value);
        Assert.Equal(expectThick, m.Groups[4].Value);
        Assert.Equal(expectTongue, m.Groups[1].Value);
        Assert.NotEqual(m.Groups[2].Value, m.Groups[4].Value);   // 板厚不是传入值（旧写法会在这里红）
    }
}
