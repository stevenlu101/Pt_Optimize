namespace PtOptimize.Core;

/// <summary>
/// 同步进度适配器：<see cref="Report"/> 在调用线程上当场执行，不经同步上下文。
/// 全仓只有这一份（<c>SyncProgressOnlyTests</c> 守：`Progress&lt;T&gt;` 会把日志打成乱序，而乱序的 trace 会让人读出错的因果）。
/// 2026-09-25：由 Program.cs 的私有副本与 ShapeSearchDriver 的私有副本合成（两份逐字同义，Windows 跑机上 SyncProgressOnlyTests 数出 2 份）。
/// </summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _h;
    public SyncProgress(Action<T> h) => _h = h ?? throw new ArgumentNullException(nameof(h));
    public void Report(T value) => _h(value);
}
