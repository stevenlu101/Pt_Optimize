using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R36（2026-09-11）：<see cref="ShapeBatchEval"/> 自己的门——与「搜形状」无关，
/// 只钉「按 lanes 限流、结果按输入顺序装回去、取消时已完成的不丢」这三件机械的事。
/// candidates/solveOne 都是纯 int，不牵 <c>DesignSpec</c>/<c>Solver</c>，秒级跑完。
/// </summary>
public class ShapeBatchEvalTests
{
    /// <summary>下标越小睡得越久 ⇒ 完成先后整个反过来；结果仍要落在自己的下标上。</summary>
    [Fact]
    public async Task 完成顺序打乱_结果仍按输入顺序对齐()
    {
        var candidates = new[] { 0, 1, 2, 3 };
        int Solve(int i, CancellationToken tok)
        {
            Thread.Sleep((candidates.Length - i) * 40);   // i 越小睡得越久
            return i * 10;
        }
        var outcome = await ShapeBatchEval.RunAsync(candidates, Solve, lanes: 4, CancellationToken.None);
        Assert.Equal(new[] { 0, 10, 20, 30 }, outcome.Results);
        Assert.All(outcome.Done, d => Assert.True(d));
    }

    /// <summary>lanes=1（串行语义）与 lanes=4（并发）在纯函数 solveOne 上必须逐元素相同——
    /// 这不是巧合，是 <see cref="ShapeBatchEval"/> 的结构保证：每个任务只写自己的下标。</summary>
    [Fact]
    public async Task 单路与四路结果逐元素相同()
    {
        var candidates = Enumerable.Range(0, 6).ToArray();
        int Solve(int i, CancellationToken tok) { Thread.Sleep(5); return i * i + 1; }

        var serial = await ShapeBatchEval.RunAsync(candidates, Solve, lanes: 1, CancellationToken.None);
        var parallel = await ShapeBatchEval.RunAsync(candidates, Solve, lanes: 4, CancellationToken.None);

        Assert.Equal(serial.Results, parallel.Results);
        Assert.Equal(serial.Done, parallel.Done);
        Assert.False(serial.Cancelled);
        Assert.False(parallel.Cancelled);
    }

    /// <summary>取消时：已经跑完的候选留着结果，跑到一半/还没排上号的候选 Done=false、
    /// 不许抛异常把已经算完的一起带走——「已经算完的形状结果不会丢」在并行路径下也要成立。</summary>
    [Fact]
    public async Task 取消时已完成的保留未完成的为空()
    {
        using var cts = new CancellationTokenSource();
        // lanes == 候选数：4 个候选同时拿到通行证，没有信号量排队的先后竞争 ——
        // 0/1 号总是立刻算完，2/3 号总是卡住等取消，结果不依赖调度器怎么排。
        int Solve(int i, CancellationToken tok)
        {
            if (i < 2) return i * 100;
            while (!tok.IsCancellationRequested) Thread.Sleep(5);
            tok.ThrowIfCancellationRequested();
            return -1;   // 到不了这里
        }

        var task = ShapeBatchEval.RunAsync(new[] { 0, 1, 2, 3 }, Solve, lanes: 4, cts.Token);
        await Task.Delay(200);   // 给 0/1 号跑完、2/3 号进入等待循环留够时间
        cts.Cancel();
        var outcome = await task;

        Assert.True(outcome.Cancelled);
        Assert.True(outcome.Done[0]); Assert.Equal(0, outcome.Results[0]);
        Assert.True(outcome.Done[1]); Assert.Equal(100, outcome.Results[1]);
        Assert.False(outcome.Done[2]);
        Assert.False(outcome.Done[3]);
    }
}
