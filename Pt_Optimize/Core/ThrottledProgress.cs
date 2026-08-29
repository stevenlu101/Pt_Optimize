using System;
using System.Diagnostics;

namespace PtOptimize.Core;

/// <summary>
/// **限流进度转发** —— 长任务里「看不出慢和挂了的区别」这件事的解药
/// （2026-08-29，用户：「跑这么长时间，进度条与提示要做好，容易误认死机」）。
///
/// ══ 病在哪
///
/// <see cref="LineRunner"/> **一直在报**（`外层耦合 3/600（ω=0.35）…`、`段 2/3：反算电流…`），
/// 但 <see cref="MeshVerify"/> 与 <see cref="Solver"/> 调它时都传了 <c>null</c> ——
/// **报的东西全扔了**。于是：
///   · 网格无关复核只在**每档开头**报一次，档内可能几小时静默；
///   · 实测 0.6 档跑到 0.146 mm 那一档时，我自己也只能靠**看内存占用**猜它还活着。
///
/// ⚠ 这不是「体验」问题：看不出死活，人就会去 kill 掉一个其实正常的几小时任务，
///   然后重跑，然后再 kill —— 代价是整天。
///
/// ══ 为什么要限流而不是直接转发
///
/// 内层一秒可能报几十条，全转出去会把真正要看的东西淹掉（而淹掉 = 等于没报）。
/// ⇒ 按时间节流，且**把期间丢掉了几条说出来** —— 不说的话，
///   读的人会以为内层就报了这么几次，从而低估它的活跃程度。
///
/// ⚠ 与 <c>Program.SyncProgress&lt;T&gt;</c> 是**两件事**，不是重复：
///   那个管**顺序**（控制台没有同步上下文，异步 Progress 会打乱序）；
///   这个管**频率**。两者可以叠：先限流，再同步转发。
/// </summary>
public sealed class ThrottledProgress : IProgress<string>
{
    private readonly IProgress<string>? _to;
    private readonly TimeSpan _gap;
    private readonly string _prefix;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private TimeSpan _last = TimeSpan.FromSeconds(-1e6);
    private int _dropped;

    /// <param name="to">真正的接收方；null 时本类整体是空操作。</param>
    /// <param name="seconds">最短间隔秒。默认 20 s —— 够稀疏不淹没，也够密集看得出活着。</param>
    /// <param name="prefix">每行前缀，用来说明「这是哪一层在报」。</param>
    public ThrottledProgress(IProgress<string>? to, double seconds = 20, string prefix = "")
    {
        _to = to;
        _gap = TimeSpan.FromSeconds(Math.Max(0.1, seconds));
        _prefix = prefix ?? "";
    }

    /// <summary>本转发器活了多久 —— 调用方拿它做耗时预测。</summary>
    public TimeSpan Elapsed => _sw.Elapsed;

    public void Report(string value)
    {
        if (_to is null) return;
        var now = _sw.Elapsed;
        if (now - _last < _gap) { _dropped++; return; }
        _last = now;
        string more = _dropped > 0 ? $"（期间另有 {_dropped} 条未显示）" : "";
        _dropped = 0;
        _to.Report($"{_prefix}[已跑 {Fmt(now)}] {value}{more}");
    }

    /// <summary>把耗时说成人话：秒 / 分 / 时分。几小时的任务用「7200 s」是不给人看的。</summary>
    public static string Fmt(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{t.TotalSeconds:0} s"
      : t.TotalMinutes < 90 ? $"{t.TotalMinutes:0} 分"
      : $"{(int)t.TotalHours} 时 {t.Minutes:00} 分";
}
