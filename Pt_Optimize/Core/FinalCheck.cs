using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// 终验要跑的三关的选项。2026-09-18，Opus 5。
/// </summary>
public sealed class FinalCheckOptions
{
    /// <summary>
    /// 判决网格（= 加密复算收敛到的那一张）。null = 不改算例自带的网格。
    /// ⚠ 三关必须在**同一张网格**上：「根的位置随网格移动」对升温里那几条场解出来的量同样成立
    /// （<see cref="RampSweepOptions.Mesh"/> 的说明）。
    /// </summary>
    public SolverOptions? Mesh;

    /// <summary>跑不跑第一关。false ⇒ 结果里照实登记「没跑」，不是「过」。</summary>
    public bool RunRamp = true;

    /// <summary>跑不跑第三关。同上。</summary>
    public bool RunEmptyTube = true;

    /// <summary>第一关的轨迹设定点 °C。null = 用 <see cref="RampSweepOptions"/> 的默认轨迹。</summary>
    public double[]? TrajectoryC;

    /// <summary>
    /// 某一关不跑时，**照实写进结果里的那句原因**（空 = 用默认那句「参数表里那一项关掉了」）。
    /// 调用方各有各的理由（参数表关掉了／图纸模式上没有那套旋钮），而「没跑」这件事必须带着理由走到
    /// 界面与安装报告上 —— 只写「没跑」会被读成「这一次碰巧没算」，写清理由才知道要怎么把它打开。
    /// </summary>
    public string SkipReason = "";
}

/// <summary>三关这一次实际跑了什么、各花了多久（机时量）。2026-09-18，Opus 5。</summary>
public sealed class FinalCheckRun
{
    public RampSweepResult? Ramp;
    public LineResult? EmptyTube;
    public double RampSeconds = double.NaN, EmptyTubeSeconds = double.NaN;
    public string[] Skipped = Array.Empty<string>();
}

/// <summary>
/// ★★★★★ **终验三关：按顺序跑齐，结论一起给**（2026-09-18，Opus 5）——
/// 这是 <see cref="RampSweep.Run"/> 的**第一个生产调用方**。
///
/// ══ 为什么要有它
///
/// 用户 2026-09-15/16 定的次序：**升温全程**先过 → **带玻璃稳态**决定法兰设计成不成
/// → **空管到温**只卡电流密度与场有效 → 再看**铂重**。
/// 而在此之前，APP 的界面只跑了中间那一关：
///   · <see cref="RampSweep.Run"/> 全仓**只有测试在调**（HANDOVER 物性接线表：「算得出、点不到」，
///     已在 <c>R48ExpansionDownstreamTests</c> 里登记成一条门）；
///   · 空管到温稳态的分工况判据（<see cref="LineResult.RequiredFor"/>）早就在，但**没人造过空管算例**。
/// 也就是说：界面按「三关都过」在说话，实际只看了一关。
/// 这正是本项目最怕的那一族 —— 不是数错了，是**算得出却没送到人手上**。
///
/// ══ 三关各自读谁
///
///   第一关 升温全程　　<see cref="RampSweep.Run"/>（逐设定点一次整线空管解；场有效 + 管与截面电流密度两条并列判；伸长只报数）
///   第二关 带玻璃稳态　**调用方传进来的那一份**（终验刚复算完的结果）—— 不重跑，重跑就有了第二个来源
///   第三关 空管到温　　<see cref="DesignSpec.BuildCase"/>(emptyTube: true) + <see cref="LineRunner.Run"/>
///   然后　 铂重　　　　带玻璃那一份的 <see cref="LineResult.TotalMassG"/>
///
/// ⚠ 牌号：第一关的膨胀按 <see cref="DesignInputs.GradeName"/> 走（用户 2026-09-15「默认纯铂，
///   工程师在 APP 特别设定才换，且要全链路生效」）。<see cref="RampSweepOptions.Grade"/> 的默认值是
///   "Pt" 的字面量 —— 生产调用方**必须**显式覆盖，否则界面上换了牌号而伸长还按纯铂算。
/// ⚠ 本类**不判任何东西**：三关的判定各在各的地方（<see cref="RampSweepResult.Verdict"/>、
///   <see cref="LineResult.AllOk"/>），这里只负责按顺序跑、把结果挂到带玻璃那一份上。
/// </summary>
public static class FinalCheck
{
    /// <summary>
    /// 按顺序跑齐三关，结果挂到 <paramref name="glass"/>（第二关那一份）上。
    /// 返回值只为了让调用方拿到「跑了什么、多久」；判定一律从 <paramref name="glass"/> 上读。
    /// </summary>
    /// <param name="spec">
    /// 已定型的设计（终验采用的那一份）。
    /// **只在两关都不跑时**允许为 null —— 那一支不碰它，本方法只把「没跑 + 理由」登记到结果上
    /// （图纸模式就走这一支：那条路上根本造不出解析设计）。要跑而给 null 一律抛。
    /// </param>
    /// <param name="inputs">参数表。</param>
    /// <param name="glass">**带玻璃稳态**那一份结果（终验刚复算完的）。三关结果挂在它身上。</param>
    public static FinalCheckRun Run(DesignSpec? spec, DesignInputs inputs, LineResult glass,
                                    FinalCheckOptions? options = null,
                                    IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));
        if (glass is null) throw new ArgumentNullException(nameof(glass));

        var opt = options ?? new FinalCheckOptions();
        if (spec is null && (opt.RunRamp || opt.RunEmptyTube))
            throw new ArgumentNullException(nameof(spec), "要跑升温全程或空管到温，就必须给设计 —— 只有两关都不跑时才允许为 null。");
        var run = new FinalCheckRun();
        var skipped = new List<string>();

        // ── 第一关：升温全程 ──
        if (opt.RunRamp)
        {
            progress?.Report($"{FinalCheckReport.Step1}：逐设定点解整线 …");
            var sw = Stopwatch.StartNew();
            var ro = new RampSweepOptions
            {
                RunClampAlt = false,
                Mesh = opt.Mesh,
                Grade = inputs.GradeName,        // ★ 牌号全链路生效（见类头 ⚠）
            };
            if (opt.TrajectoryC is { Length: > 0 }) ro.TrajectoryC = opt.TrajectoryC;
            run.Ramp = RampSweep.Run(spec!, inputs, ro, progress, cancel);
            sw.Stop();
            run.RampSeconds = sw.Elapsed.TotalSeconds;
        }
        else skipped.Add($"{FinalCheckReport.Step1}：{Why(opt)}");

        cancel.ThrowIfCancellationRequested();

        // ── 第三关：空管到温稳态（第二关就是传进来的 glass，不重跑）──
        if (opt.RunEmptyTube)
        {
            progress?.Report($"{FinalCheckReport.Step3}：解一次整线（无玻璃）…");
            var sw = Stopwatch.StartNew();
            var lcEmpty = spec!.BuildCase(inputs, emptyTube: true);
            if (opt.Mesh is not null) Solver.ApplyCaseMesh(lcEmpty, opt.Mesh);
            LineResult empty;
            try { empty = LineRunner.Run(lcEmpty, progress, cancel); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { empty = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}", EmptyTube = true }; }
            sw.Stop();
            run.EmptyTube = empty;
            run.EmptyTubeSeconds = sw.Elapsed.TotalSeconds;
        }
        else skipped.Add($"{FinalCheckReport.Step3}：{Why(opt)}");

        run.Skipped = skipped.ToArray();

        // ── 挂上去（新状态位要在下游读得到；门造在下游：R48MThreeStateWiringTests）──
        glass.RampSweep = run.Ramp;
        glass.RampSeconds = run.RampSeconds;
        glass.EmptyTubeSteady = run.EmptyTube;
        glass.EmptyTubeSeconds = run.EmptyTubeSeconds;
        glass.ThreeStateSkipped = run.Skipped;
        return run;
    }

    /// <summary>没跑的理由：调用方给了就用它的，没给就用参数表那一项那句（写法只有这一处）。</summary>
    private static string Why(FinalCheckOptions opt)
        => opt.SkipReason.Length > 0
           ? opt.SkipReason
           : $"参数表里「{FinalCheckReport.SwitchLabel}」关掉了 —— **没跑不等于过**";
}
