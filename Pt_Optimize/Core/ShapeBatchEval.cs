using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PtOptimize.Core;

/// <summary>
/// R36（2026-09-11）：「◇ 搜形状」并行化的唯一落点。
///
/// ══ 为什么只有这一个类，且只有这一条方法
///
/// `deliverable/搜形状并行化_审查_2026-09-09.md`（只读审查，2026-09-09）核过整条
/// `Solver.Solve → LineRunner → SegmentSolver/CoupledSolver → ShellMesh/ShellCurrent/
/// ShellThermal → MeshVerify/DesignCurrent/SectionSizing/RemovalPriority` 链路：
/// 没有一处会被「搜形状」碰到的非线程安全共享状态（唯一那处 `Geometry3dm._tfCache`
/// 只有 `.3dm` 路径会碰，搜形状强制走 analytic 分支，够不着）；`SolverResult`/`LineResult`
/// 全是实例类，`Solver.Solve` 入口先 `geometry.Clone()`，不改调用方传入的 `DesignSpec`。
/// ⇒ **`solveOne` 只要是纯函数（同输入⇒同输出，不碰任何共享可变状态），并发跑
/// 与逐个跑给出的结果就该逐位相同**——这不是这个类要去验证的事，是它成立的前提；
/// 这个类只负责「按 `lanes` 限流、把结果按输入顺序装回去」这一件机械的事。
///
/// ══ 只用在批内候选互相独立的地方
///
/// 审查第 0 节结论：`SearchShapeAsync`（`UI/LineDesignPage.cs`）八个阶段里，只有
/// ⑥「剩余舌宽比例」（≤2 点）与 ⑦「邻域探索」（`ShapeSearchPlan.Neighbours` 固定
/// 4 点）是**本轮内互相独立**的；③④⑤（不动点迭代／二分／黄金分割）是教科书式的
/// 逐次依赖，下一个探针的坐标要用上一个探针的结果算，结构上不可并行——这个类
/// 不该被用在那三段，用了也没有意义（候选之间本来就要串行产生）。
///
/// ══ 取消语义：不吞掉「已经算完的」
///
/// 用户原话（见 HANDOVER「随时可以点取消，已经算完的形状结果不会丢」）——如果
/// `RunAsync` 在取消时直接抛 `OperationCanceledException`，调用方的 `await` 会连
/// 那些已经跑完的候选结果一起丢掉（异常只带得走一条消息，带不走一个数组）。
/// 所以这里选**返回一个带「哪些完成了」的结构**（不是直接抛）：`Outcome.Done[i]`
/// 说第 i 个候选跑完没有，跑完的 `Results[i]` 有值；调用方把跑完的先按顺序收尾
/// （`rows.Add`／`_out.AppendText`），再看 `Outcome.Cancelled` 决定要不要自己
/// `ct.ThrowIfCancellationRequested()` 把取消继续往上传——这样「已经算完的不会丢」
/// 这句话在并行路径下依然成立。
/// </summary>
public static class ShapeBatchEval
{
    /// <summary>
    /// 一批并发求解的结果，按**输入顺序**对齐（不是完成先后）——因为每个任务
    /// 直接写自己那个下标 <c>results[idx]</c>，谁先算完不影响它落在数组里的位置。
    /// </summary>
    public sealed class Outcome<TResult>
    {
        /// <summary>第 i 个候选的结果；<see cref="Done"/>[i] 为 false 时这里是 default——
        /// 是不是「真的算出来了」一律看 <see cref="Done"/>，不看这里是不是 null
        /// （<typeparamref name="TResult"/> 未必约束成引用类型，值类型的 default 不是 null）。</summary>
        public required TResult[] Results { get; init; }

        /// <summary>第 i 个候选跑完了没有——没排到锁（取消先发生）或跑到一半被取消都算 false。</summary>
        public required bool[] Done { get; init; }

        /// <summary>本批是否被取消打断过（`ct.IsCancellationRequested`）。为 true 时
        /// <see cref="Done"/> 里可能有 false——调用方处理完已完成的之后自己决定要不要
        /// 把取消继续往上抛。</summary>
        public bool Cancelled { get; init; }
    }

    /// <summary>
    /// 并发最多 <paramref name="lanes"/> 路跑 <paramref name="solveOne"/>，结果按
    /// <paramref name="candidates"/> 的下标装回 <see cref="Outcome{TResult}.Results"/>。
    ///
    /// <paramref name="lanes"/> == 1 就是串行语义——不是另开一条分支，只是信号量
    /// 容量恰好为 1，同一时刻只放一个任务进去，走的是**同一段代码**。
    /// </summary>
    /// <param name="candidates">候选列表；顺序就是结果要对齐的顺序。</param>
    /// <param name="solveOne">纯函数：同一个候选、同一个 <see cref="CancellationToken"/>
    /// 状态下必须给出同一个结果——这是本类"并行不改变结果"成立的前提，调用方要自己守住
    /// （不要在这里面碰共享可变状态、UI 控件、静态可写字段）。</param>
    /// <param name="lanes">最大并发数，≥ 1。</param>
    /// <param name="ct">取消令牌；多个候选安全地共享同一个（<see cref="CancellationToken"/>
    /// 本身按设计允许多线程同时读）。</param>
    public static async Task<Outcome<TResult>> RunAsync<TCandidate, TResult>(
        IReadOnlyList<TCandidate> candidates,
        Func<TCandidate, CancellationToken, TResult> solveOne,
        int lanes,
        CancellationToken ct)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (solveOne is null) throw new ArgumentNullException(nameof(solveOne));
        if (lanes < 1) throw new ArgumentOutOfRangeException(nameof(lanes), "lanes 至少 1（= 串行，不是另一条代码路径）。");

        int n = candidates.Count;
        var results = new TResult[n];
        var done = new bool[n];
        if (n == 0)
            return new Outcome<TResult> { Results = results, Done = done, Cancelled = ct.IsCancellationRequested };

        using var gate = new SemaphoreSlim(lanes, lanes);

        async Task RunOneAsync(int idx)
        {
            try { await gate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }   // 没排上号：取消先到了，done[idx] 留 false
            try
            {
                // ★ Task.Run 不传 ct：真正的取消必须由 solveOne 自己通过 ct 观察并抛出
                //   （Solver.Solve 内部各处 `cancel.ThrowIfCancellationRequested()` 正是这样做的）。
                //   若这里也把 ct 传给 Task.Run 本身，任务可能在**还没排上线程池**时就被判取消，
                //   那样连"有没有跑过 solveOne"都说不清，Done 的含义会变得含糊。
                results[idx] = await Task.Run(() => solveOne(candidates[idx], ct)).ConfigureAwait(false);
                done[idx] = true;
            }
            catch (OperationCanceledException) { /* 跑到一半被取消：done[idx] 留 false，结果不算数 */ }
            finally { gate.Release(); }
        }

        var tasks = new Task[n];
        for (int i = 0; i < n; i++) tasks[i] = RunOneAsync(i);
        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new Outcome<TResult> { Results = results, Done = done, Cancelled = ct.IsCancellationRequested };
    }
}
