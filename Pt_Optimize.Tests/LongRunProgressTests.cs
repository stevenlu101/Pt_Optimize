using System;
using System.Collections.Generic;
using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **几小时的任务必须看得出还活着**
/// （2026-08-29 用户：「跑这么长时间，进度条与提示要做好，容易误认死机」）。
///
/// ══ 病在哪（实测，不是设想）
///
/// <see cref="LineRunner"/> **一直在报**（`外层耦合 3/600（ω=0.35）…`、`段 2/3：反算电流…`），
/// 但 <see cref="MeshVerify"/> 与 <see cref="Solver"/> 调它时都传 <c>null</c> ——
/// **报的东西全被扔掉**。于是网格无关复核只在每档开头报一次，档内几小时静默。
///
/// 2026-08-29 实测：0.6 档复核跑到 0.146 mm 那一档时，
/// **我自己也只能靠看进程内存占用猜它还活着**。
///
/// ⚠ 这不是「体验」问题：看不出死活，人就会去 kill 掉一个其实正常的几小时任务，
///   然后重跑，然后再 kill —— 代价是整天。
///
/// ══ 三件事
///
/// ① **限流转发**内层进度（全转会淹掉日志，而淹掉等于没报）；
/// ② 每档**先说这一档有多大、预计多久**（单元数 ∝ 1/h²，每加密一档约 ×4，
///    用**上一档的实测耗时**推下一档，比拍脑袋准，推错了还会自己纠正）；
/// ③ 每档**跑完报实测耗时与单元数**，让下一次的预测有依据。
/// </summary>
public class LongRunProgressTests
{
    private static string Core(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", f));

    /// <summary>★ 内层进度不许再被扔掉 —— <c>LineRunner.Run(.., null, ..)</c> 是病灶本身。</summary>
    [Fact]
    public void 内层进度不再被扔掉()
    {
        foreach (var f in new[] { "MeshVerify.cs", "Solver.cs" })
        {
            string s = Core(f);
            // ★★ 门**不许认变量名**：2026-08-29 中带确认那一处叫 `lcC`，
            //   写死 `lc` 的门就从缝里漏了过去 —— 实测静默 29 分钟没有一行输出。
            //   一条只挡得住自己当初那一行的门，等于没有门。
            var m = System.Text.RegularExpressions.Regex.Match(
                s, @"LineRunner\.Run\(\s*\w+\s*,\s*null\s*[,)]");
            Assert.False(m.Success,
                $"{f} 里还有把内层进度扔掉的调用：{m.Value} —— 几小时的任务会整段静默");
            Assert.Contains("ThrottledProgress", s);
        }
    }

    /// <summary>限流：内层一秒几十条，全转等于没报。</summary>
    [Fact]
    public void 限流器只在间隔之后放行()
    {
        var got = new List<string>();
        var t = new ThrottledProgress(new Sink(got), seconds: 3600);
        for (int i = 0; i < 50; i++) t.Report("x" + i);
        Assert.Single(got);                       // 第一条放行，其余被压
        Assert.Contains("x0", got[0]);
    }

    /// <summary>★ 被压掉几条要**说出来** —— 不说，读的人会低估内层的活跃程度。</summary>
    [Fact]
    public void 压掉的条数要报出来()
    {
        var got = new List<string>();
        var t = new ThrottledProgress(new Sink(got), seconds: 0.1);
        t.Report("a");
        for (int i = 0; i < 5; i++) t.Report("drop" + i);
        System.Threading.Thread.Sleep(150);
        t.Report("b");
        Assert.Equal(2, got.Count);
        Assert.Contains("另有 5 条未显示", got[1]);
    }

    /// <summary>每行都要带「已跑多久」—— 那是判断死活最直接的一个数。</summary>
    [Fact]
    public void 每行都带已跑时长()
    {
        var got = new List<string>();
        new ThrottledProgress(new Sink(got)).Report("hello");
        Assert.Contains("已跑", got[0]);
    }

    /// <summary>耗时说人话：几小时的任务不许只给「7200 s」。</summary>
    [Fact]
    public void 耗时说人话()
    {
        Assert.Equal("30 s", ThrottledProgress.Fmt(TimeSpan.FromSeconds(30)));
        Assert.Equal("15 分", ThrottledProgress.Fmt(TimeSpan.FromMinutes(15)));
        Assert.Equal("2 时 30 分", ThrottledProgress.Fmt(TimeSpan.FromMinutes(150)));
    }

    /// <summary>接收方为 null 时整体是空操作 —— 不许在没人听的时候还去格式化字符串。</summary>
    [Fact]
    public void 没有接收方时是空操作()
    {
        var t = new ThrottledProgress(null);
        t.Report("x");            // 不炸即可
        Assert.True(t.Elapsed >= TimeSpan.Zero);
    }

    /// <summary>
    /// ★ 每档要**先说预计多久**（由上一档实测推），跑完要**报实测**。
    /// 少了预测，人不知道该等 3 分钟还是 3 小时；少了实测，下一次预测就没依据。
    /// </summary>
    [Fact]
    public void 每档先给预计跑完报实测()
    {
        string s = Core("MeshVerify.cs");
        Assert.Contains("本档估 **~", s);
        Assert.Contains("按上两档实测耗时比 ×", s); Assert.Contains("**偏乐观**", s);
        Assert.Contains("完成 ——", s);
        Assert.Contains("ThrottledProgress.Fmt(swOne.Elapsed)", s);
    }

    /// <summary>
    /// ★★ **中间结果要落地** —— 每档的判据值当场就报，不许攒到整趟结束。
    ///
    /// 2026-08-29 实测：0.6 档细阶梯三档全部算完、已跑 4 时 22 分，
    /// 而日志里**一个判据数字都没有**（只有单元数与耗时）—— 被 kill 掉就是四小时全丢。
    /// 「看得出还活着」只解决了一半；另一半是**算出来的东西要立刻落地**。
    /// </summary>
    [Fact]
    public void 每档的判据值当场落地()
    {
        string s = Core("MeshVerify.cs");
        Assert.Contains("②′ {a2p:0.000} W", s);
        Assert.Contains("②″ {a2pp:0.000} K", s);
        Assert.Contains("③ {a3:0.000} K", s);
        Assert.Contains("较上一档：", s);
    }

    /// <summary>
    /// ★ 中带确认是整趟里**最贵的单步**（实测 ≥ 3 时 43 分），
    /// 它必须报进度、必须给下界估时 —— 此前两样都没有，实测静默 29 分钟。
    /// </summary>
    [Fact]
    public void 中带确认有进度也有估时()
    {
        string s = Core("MeshVerify.cs");
        Assert.Contains("var rc = LineRunner.Run(lcC, innerC, cancel);", s);
        Assert.Contains("**至少 {floorC}**", s);
        Assert.Contains("这是下界，不是估计", s);
        Assert.Contains("中带确认：场解完成 —— 用时", s);
    }

    private sealed class Sink : IProgress<string>
    {
        private readonly List<string> _to;
        public Sink(List<string> to) => _to = to;
        public void Report(string value) => _to.Add(value);
    }
}
