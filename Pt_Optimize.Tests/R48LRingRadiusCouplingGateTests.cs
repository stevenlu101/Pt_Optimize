using System;
using System.IO;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路：**内级/外级台阶半径的耦合上界** —— 门。2026-09-17，Opus 5。
//
//  ══ 病（实测，deliverable/R48_L_端到端_细网格_W08_本次开跑于2026-09-17_093013.txt）
//  W08 细网格档第一遍：求解器把**内级环宽 r₁** 抬到它的常数上界 10 mm ⇒
//    r₁ = 管孔 25.8 + 10 = **35.8 mm**，而外级还停在默认 r₂ = 孔 + 2×3 = **31.8 mm**
//  ⇒ r₁ > r₂，DesignSpec.RingRadiiOf 抛「第 1 片的环台阶半径没有递增」，那一次场解整个失败。
//  求解器把它渲染成「这根旋钮抬到上界时解不出来」并自评「该修的是这根旋钮的上界」。
//  后果不止白花一次场解：那根旋钮被当成**不成立的候选**淘汰 ⇒ 片1「最热铂高出热偶读数」
//  报「法兰侧候选都不成立」⇒ 结构性停机 ⇒ 细网格第二遍**按设计不做**。
//  **一个上界写错，读出来的是「这个设计不可行」。**
//
//  ══ 修
//  内级环宽的上界 = **当前外级环宽 − 一格图纸格**；外级环宽的上界 = **盘径 − 孔径 − 一格图纸格**。
//  两个「当前值」都从几何件读（<see cref="DesignSpec.RingWidthsOf"/>），不在求解器里手抄默认规则。
//
//  ══ 这道门守什么
//   ① 造出「内级要越过外级」的抬升 ⇒ **被上界截住**，且几何造得出来、不抛异常。
//   ② **改回旧上界（常数 10）⇒ 红**：门里当场用旧上界造一次几何，它必须抛 ——
//      病是真的；同时上界必须严格小于旧常数，谁把耦合约束删掉这一条就红。
//   ③ **只截该截的**：外级本来就宽（16 mm）时，内级上界要原样等于选项上界，不许顺手收紧。
//   ④ 环宽的默认规则只有几何件那一份：RingWidthsOf 与 RingRadiiOf 必须逐位一致。
//  上界一律从公开读口 <see cref="Solver.KnobUpperBound"/> 取 —— **门不许手抄生产配方**。
// ════════════════════════════════════════════════════════════════════════════

public class R48LRingRadiusCouplingGateTests
{
    private readonly ITestOutputHelper _o;
    public R48LRingRadiusCouplingGateTests(ITestOutputHelper o) { _o = o; }

    /// <summary>出病的那一片（W08 第一遍报的就是片 1）。</summary>
    private const int J = 1;

    [Fact]
    public void 内级半径的上界必须被外级截住()
    {
        var p = new DesignInputs();
        var o = new SolverOptions { MaxRounds = 40, AllowTabCuts = false };
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);

        W("R48 L 路　**内级/外级台阶半径的耦合上界**　门 + 实测");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("");
        W("═══════ 病（出处：deliverable/R48_L_端到端_细网格_W08_本次开跑于2026-09-17_093013.txt）═══════");
        W("W08 细网格档第一遍：内级环宽 r₁ 抬到常数上界 10 ⇒ r₁ = 孔 25.8 + 10 = 35.8 > 外级 r₂ 31.8 ⇒");
        W("DesignSpec.RingRadiiOf 抛「第 1 片的环台阶半径没有递增」⇒ 那根旋钮被当成不成立的候选淘汰 ⇒");
        W("片1「最热铂高出热偶读数」报「法兰侧候选都不成立」⇒ 结构性停机 ⇒ 细网格第二遍按设计不做。");
        W("");

        // ── 造出「内级要越过外级」的局面：W08 原样（外级走默认 2×环宽 = 6 mm），把内级倍率抬起来造出台阶
        var d = DesignSpec.W08.Clone();
        d.RingMul[J] = 2.50;                 // 有台阶，内级半径这根旋钮才不是空转
        var w = d.RingWidthsOf(J);
        // ★ 2026-09-17 Opus 5：图纸格也从公开读口取（Solver.KnobQuantum）—— 门里不抄「哪根旋钮走哪张格子」。
        double band = Solver.KnobQuantum(o, Solver.Knob.RingR1);   // 半径走板厚那张图纸格
        W("═══════ 局面（W08 原样，只把第 1 片的环倍率 t₁ 抬到 2.50 造出台阶）═══════");
        W($"管孔半径 {d.HoleRadiusMm:0.###} mm　圆盘半径 {d.DiscRadiusMm:0.###} mm　图纸格 {band:0.###} mm");
        W($"当前环宽：内级 {w[0]:0.###} mm、外级 {w[1]:0.###} mm　⇒ 半径 r₁ {d.RingRadiiOf(J)[0]:0.###}、r₂ {d.RingRadiiOf(J)[1]:0.###} mm");
        W("");

        double hiR1 = Solver.KnobUpperBound(d, p, o, Solver.Knob.RingR1, J);
        double hiR2 = Solver.KnobUpperBound(d, p, o, Solver.Knob.RingR2, J);
        W("═══════ 上界（从公开读口 Solver.KnobUpperBound 取，门里不另抄一份配方）═══════");
        W("旋钮\t选项上界 mm\t几何上界 mm\t实际上界 mm\t被谁截住");
        W($"内级环宽 r₁\t{o.RingR1HiMm:0.###}\t{w[1] - band:0.###}（外级 − 一格图纸格）\t{hiR1:0.###}\t{(hiR1 < o.RingR1HiMm - 1e-12 ? "**外级**" : "选项")}");
        W($"外级环宽 r₂\t{o.RingR2HiMm:0.###}\t{d.DiscRadiusMm - d.HoleRadiusMm - band:0.###}（盘径 − 孔径 − 一格图纸格）\t{hiR2:0.###}\t"
          + $"{(hiR2 < o.RingR2HiMm - 1e-12 ? "**盘径**" : "选项")}");
        W("");

        // ① 被上界截住：抬到上界之后几何仍然成立（严格递增、造得出算例）
        var dHi = d.Clone();
        Solver.SetKnob(dHi, Solver.Knob.RingR1, J, hiR1, p, null);
        var rr = dHi.RingRadiiOf(J);                       // 不抛就是不抛 —— 抛了这一行当场红
        _ = dHi.BuildCase(p);                              // 整线算例也要造得出来
        W("═══════ ① 抬到上界之后（内级）═══════");
        W($"内级环宽 → {hiR1:0.###} mm　⇒ r₁ {rr[0]:0.###} mm、r₂ {rr[1]:0.###} mm　严格递增：{(rr[1] > rr[0] ? "是" : "**否**")}　"
          + "RingRadiiOf 没抛、BuildCase 造得出算例。");
        Assert.True(hiR1 < o.RingR1HiMm - 1e-12,
            $"内级环宽的上界还是常数 {o.RingR1HiMm:0.###}（没被外级 {w[1]:0.###} 截住）—— 耦合约束被删掉了");
        Assert.True(hiR1 <= w[1] - band + 1e-12,
            $"内级环宽上界 {hiR1:0.###} 没留出一格图纸格：外级 {w[1]:0.###}、图纸格 {band:0.###}");
        Assert.True(rr[1] > rr[0], $"抬到上界之后半径仍不递增：r₁={rr[0]:0.###}、r₂={rr[1]:0.###}");

        // 求解器落地时还会往图纸格上取整（Solver 的 snap）—— 取整之后也不许越过
        double snapped = Math.Min(Solver.SnapUpToGridMm(hiR1, band), hiR1);   // 与生产同一份对齐写法
        var dSnap = d.Clone();
        Solver.SetKnob(dSnap, Solver.Knob.RingR1, J, snapped, p, null);
        var rs = dSnap.RingRadiiOf(J);
        W($"落到图纸格之后 {snapped:0.###} mm ⇒ r₁ {rs[0]:0.###}、r₂ {rs[1]:0.###} mm　严格递增：{(rs[1] > rs[0] ? "是" : "**否**")}");
        Assert.True(rs[1] > rs[0], "落到图纸格之后半径不递增 —— 量化那一步把上界越过去了");
        W("");

        // ② 改回旧上界 ⇒ 红：当场用旧常数造一次，它必须抛
        var dOld = d.Clone();
        Solver.SetKnob(dOld, Solver.Knob.RingR1, J, o.RingR1HiMm, p, null);
        var boom = Assert.Throws<InvalidOperationException>(() => dOld.RingRadiiOf(J));
        W("═══════ ② 改回旧上界（常数）═══════");
        W($"内级环宽 → {o.RingR1HiMm:0.###} mm（旧上界）⇒ RingRadiiOf **抛异常**：{boom.Message}");
        W("⇒ 病是真的：旧上界造出来的几何本身不成立。这一条同时守住「谁把耦合约束删掉，这道门就红」。");
        W("");

        // ③ 只截该截的：外级本来就宽时，内级上界原样等于选项上界
        var dWide = d.Clone();
        dWide.RingW2Mm[J] = 16.0;                          // 外级已经很宽 ⇒ 几何这条不再是紧的那条
        double hiWide = Solver.KnobUpperBound(dWide, p, o, Solver.Knob.RingR1, J);
        W("═══════ ③ 只截该截的（外级 16 mm 时，内级上界应原样 = 选项上界）═══════");
        W($"外级环宽 {dWide.RingWidthsOf(J)[1]:0.###} mm ⇒ 内级上界 {hiWide:0.###} mm（选项上界 {o.RingR1HiMm:0.###}）");
        Assert.Equal(o.RingR1HiMm, hiWide, 9);
        W("⇒ 耦合约束只在它真的紧的时候生效，不顺手收紧旋钮的量程。");
        W("");

        // ④ 环宽默认规则只有几何件那一份
        W("═══════ ④ 环宽的默认规则只有几何件那一份 ═══════");
        W("片\tRingWidthsOf 内级\tRingRadiiOf 内级 − 孔\tRingWidthsOf 外级\tRingRadiiOf 外级 − 孔");
        for (int q = 0; q < d.TabThickMm.Length; q++)
        {
            var ww = d.RingWidthsOf(q);
            var radii = d.RingRadiiOf(q);
            W($"{q}\t{ww[0]:R}\t{radii[0] - d.HoleRadiusMm:R}\t{ww[1]:R}\t{radii[1] - d.HoleRadiusMm:R}");
            Assert.Equal(ww[0], radii[0] - d.HoleRadiusMm, 12);
            Assert.Equal(ww[1], radii[1] - d.HoleRadiusMm, 12);
            // 求解器读旋钮读的也必须是同一份
            Assert.Equal(ww[0], Solver.KnobValue(d, Solver.Knob.RingR1, q), 12);
            Assert.Equal(ww[1], Solver.KnobValue(d, Solver.Knob.RingR2, q), 12);
        }
        W("⇒ 几何件、求解器读旋钮、上界三处读的是同一份默认规则。");
        W("");

        // ⑤ 外级对盘径：**先查前提再谈因果** —— 这一条查完决定「不加」，实测留在这里
        W("═══════ ⑤ 外级对盘径：为什么这一侧**没有**对称地加上界（查过之后才决定的）═══════");
        W($"W08 现状：外级 r₂ = {d.RingRadiiOf(J)[1]:0.###} mm **已经在圆盘半径 {d.DiscRadiusMm:0.###} mm 之外**。");
        W("直觉的说法是「盘缘之外的台阶半径没有效果，所以上界该收到盘缘」。**先量再说**：");
        W("PlateCurrent2D.ThicknessAt 先判舌片、判不出舌片才按半径查阶梯 ⇒ 结论取决于**舌片有没有自己的厚度**。");
        W("");
        double floor = d.DiscFloorMm(p);
        W("采样：x −160…40、z −40…40 步长 1 mm，**只取板上真有金属的点**（FlangePlate.Inside —— 门里不另写一份外形）。");
        W("舌片厚\t外级 r₂ 6.0 mm vs 16.0 mm 的最大板厚差 mm\t板上采样点数\t含义");
        double wSet = MaxThickDiff(d, J, floor, 3.51, out int nSet);
        double wNaN = MaxThickDiff(d, J, floor, double.NaN, out int nNaN);
        W($"3.51 mm（求解器每遍开头闭式定的那个）\t**{wSet:R}**\t{nSet}\t盘缘之外挪外级，**一点厚度都不改**");
        W($"NaN（与基板同厚，四份内置记录就是这样）\t**{wNaN:R}**\t{nNaN}\t阶梯**管到舌片上**，盘缘之外挪外级照样改几何");
        Assert.Equal(0.0, wSet, 12);
        Assert.True(wNaN > 1e-9, "舌片厚为 NaN 时，盘缘之外的外级半径居然也没有效果 —— 那这条前提要重查");
        W("");
        W("⇒ 「r₂ 超过盘径就没有效果」**只在舌片有自己的厚度时成立**。写成无条件上界会**静默改掉**");
        W("　另一类设计（舌片与基板同厚）的几何 —— 正是本项目最怕的那种形状。");
        W($"　⇒ 本轮**不加**这条：外级环宽的上界仍是选项常数 {o.RingR2HiMm:0.###} mm（实测 {hiR2:0.###} mm，逐位相同）。");
        Assert.Equal(o.RingR2HiMm, hiR2, 9);
        W("　要不要加（例如「舌片有自己的厚度时才收到盘缘」），留给下一轮按这两行实测定。");
        W("");
        W("═══════ 一句话 ═══════");
        W($"内级环宽的上界从常数 {o.RingR1HiMm:0.###} 收到 **{hiR1:0.###} mm**（= 当前外级 {w[1]:0.###} − 一格图纸格 {band:0.###}），"
          + "抬到上界的几何严格递增、造得出算例、不抛异常；旧上界当场复现抛异常。");

        W("═══════ 仍开放（本轮查到、没动）═══════");
        W($"· W08 内置几何里 r₂ = {d.RingRadiiOf(J)[1]:0.###} mm 本来就在圆盘半径 {d.DiscRadiusMm:0.###} mm 之外。");
        W("　Geometry3dm 出图时把各级半径拼成 RingRadiiOf(j) ∪ {圆盘半径}，这一列在本形状上**不是递增的**");
        W("　（28.8 / 31.8 / 30）。本轮没碰出图那条路，只记在这里。");
        W("");

        sb.AppendLine();
        sb.AppendLine($"── 跑完 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("出处：上界 = Solver.KnobUpperBound（生产路径同一处 HiOfFor）；环宽默认规则 = DesignSpec.RingWidthsOf；"
                    + "递增检查 = DesignSpec.RingRadiiOf。门里没有第二份配方。");
        _o.WriteLine(sb.ToString());

        // ★ 这是**快门**，每跑一次全套都会跑到它 —— 无条件往 deliverable/ 写一份带开跑时刻的报告，
        //   等于同一件事在交付目录里堆好几份「本次开跑于」，正是最容易让人读错的形状。
        //   ⇒ 平时只印在测试输出里；要那份报告时设 R48L_REPORT=1 再跑，数逐位相同（门本身就在守这些数）。
        if (Environment.GetEnvironmentVariable("R48L_REPORT") == "1")
        {
            string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"R48_L_台阶半径耦合上界_本次开跑于{stamp}.txt"),
                              sb.ToString(), new UTF8Encoding(true));
        }
    }

    /// <summary>
    /// 同一片、只把**外级环宽**从 6.0 换到 16.0（两者都在圆盘半径之外），逐点比板厚，回报最大差。
    /// <paramref name="tongueMm"/> = 舌片自己的厚度（NaN = 与基板同厚）—— 这一条正是结论的分水岭。
    /// </summary>
    private static double MaxThickDiff(DesignSpec d, int j, double floorMm, double tongueMm, out int probed)
    {
        var a = d.Clone(); a.TongueThickMm[j] = tongueMm; a.RingW2Mm[j] = 6.0;
        var b = d.Clone(); b.TongueThickMm[j] = tongueMm; b.RingW2Mm[j] = 16.0;
        var pa = a.Plate(j, floorMm);
        var pb = b.Plate(j, floorMm);
        double worst = 0; probed = 0;
        for (double x = -160; x <= 40.0001; x += 1)
            for (double z = -40; z <= 40.0001; z += 1)
            {
                // 「这里有没有金属」只有 FlangePlate.Inside 一份判据 —— 门里不另写一份外形
                if (!pa.Inside(x, z) || !pb.Inside(x, z)) continue;
                double ta = pa.ThicknessAt(x, z), tb = pb.ThicknessAt(x, z);
                if (double.IsNaN(ta) || double.IsNaN(tb)) continue;
                probed++;
                worst = Math.Max(worst, Math.Abs(ta - tb));
            }
        return worst;
    }
}
