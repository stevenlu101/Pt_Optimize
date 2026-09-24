using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 搜形状 Core 驱动（<see cref="ShapeSearchDriver"/>）的整夜实跑器（2026-09-23 业主「让搜形状里面这几套算法真正跑起来」）。
///
/// 只印不判：本门不判答案对不对，只把每个形状一行实时追加进证据档（边跑边 flush，进程被杀也留下已算完的），
/// 末尾印方案卡或「无可行形状」报告。断言只核「跑完了、末尾有卡或报告」。
///
/// 种子 = <see cref="R48NMeshGateTests.Design"/>（W08 导航档复原／W06 细网格档复原），工艺参数 = new DesignInputs()。
/// 族：只跑「不挖舌孔」一族（AllowTabCuts = false）。出处：现役设计的解法设定 = 界面下拉预设第 0 项，
///   与 R48LEndToEndTests.ProductionOptions（MaxRounds 40、AllowTabCuts false）同一份；两族并跑是界面「两个都算」的行为，本跑器不做（写明）。
///
/// 环境变量（缺省值出处）：
///   SHAPE_MAXDISC 盘半径上端 mm，缺省 50（探针给的表，无出处）；写成 lb+x 表示「起点表第一点 + x」（冒烟用；
///                 第一点 = 判据「圆盘盖得住管孔＋焊脚」的闭式下界按 ShapeSearchPlan.LiveDiscs 的 0.5 mm 向上取整）。
///   SHAPE_LANES   批内并发路数，缺省 2（4 核机器，给同机别的长跑留两核；选定）。
///   SHAPE_SCREEN  粗筛轮数，缺省 = ShapeSearchOptions.ScreenRounds（2026-09-24 起 8，选定；依据夜跑 001304／021617，界面 16 轮在 4 核 Linux 上一形状 30 h 以上）。
///   SHAPE_PARALLEL 1／0：① 并行首遍（起点表全部点并行解一遍），缺省 1（ShapeSearchOptions.ParallelFirstPass；0 = 界面原算法）。
///   SHAPE_SKIPGROW 1／0：粗筛终局复核后不放大重做，缺省 1（ShapeSearchOptions.ScreenSkipRadiusGrowth；0 = 照旧放大重做）。
///   SHAPE_REUSE   1／0：粗筛同状态整线解复用，缺省 1（ShapeSearchOptions.ScreenReuseSameState；0 = 改前逐位）。
///   SHAPE_SHOULDER 1／0：闭式肩部预筛，缺省 1（ShapeSearchOptions.ShoulderJPrescreen；0 = 不预筛）。
///   SHAPE_WFRAC   舌宽比表，逗号分隔，缺省 = ShapeSearchOptions.WFrac（2026-09-24 起 1,0.875,0.75；改回 0.75,1）。
///   SHAPE_FINAL   精算轮数，缺省 40（照抄界面）。
///   SHAPE_COARSE  粗筛平坦区网格 mm，缺省 0（照抄界面，关）。
///   SHAPE_SEEDFIRST 1／0：是否先算种子自己的形状作基准，缺省 1（照抄界面）。
///   SHAPE_MAXEXTEND 邻域爬山最多几轮，缺省 6（照抄界面 SearchMaxExtend）。
///   SHAPE_CONTENTION 同机并跑说明（原样印进证据头），缺省「未申报」。
/// </summary>
[Trait("速度", "慢")]
public class R48ShapeSearchRunTests
{
    internal static double EnvD(string name, double dflt)
    {
        string? s = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(s) ? dflt : double.Parse(s, CultureInfo.InvariantCulture);
    }

    internal static int EnvI(string name, int dflt)
    {
        string? s = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(s) ? dflt : int.Parse(s, CultureInfo.InvariantCulture);
    }

    /// <summary>上端：数字，或 lb+x（起点表第一点 + x）。返回（值, 写进证据头的出处说明）。</summary>
    internal static (double Mm, string Source) MaxDisc(double lowerBoundMm)
    {
        string? s = Environment.GetEnvironmentVariable("SHAPE_MAXDISC");
        if (string.IsNullOrWhiteSpace(s)) return (50.0, "探针给的表，无出处（缺省 50 mm）");
        s = s.Trim();
        if (s.StartsWith("lb+", StringComparison.OrdinalIgnoreCase))
        {
            double x = double.Parse(s.Substring(3), CultureInfo.InvariantCulture);
            return (lowerBoundMm + x, $"探针给的表，无出处（SHAPE_MAXDISC={s}：起点表第一点 {lowerBoundMm.ToString("0.000", CultureInfo.InvariantCulture)} + {x.ToString("0.###", CultureInfo.InvariantCulture)}，冒烟用）");
        }
        return (double.Parse(s, CultureInfo.InvariantCulture), $"探针给的表，无出处（SHAPE_MAXDISC={s}）");
    }

    /// <summary>起点表第一点：闭式下界按 <see cref="ShapeSearchPlan.LiveDiscs"/> 的 0.5 mm 向上取整（与驱动缺省表同一条规则、同一份实现）。</summary>
    internal static double GridStart(DesignSpec seed, DesignInputs baseIn)
        => ShapeSearchPlan.LiveDiscs(new[] { double.NegativeInfinity },
               GeometryScreen.MinDiscRadiusMm(holeRadiusMm: seed.HoleRadiusMm, thickMm: baseIn.WeldMinThicknessMm, wallMm: seed.WallMm))[0];

    internal static string Git(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = HandoverDoc.Root() };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            return string.IsNullOrEmpty(o) ? "未查到" : o;
        }
        catch (Exception ex) { return "未查到（" + ex.GetType().Name + "）"; }
    }

    internal static string LoadAvg()
    {
        try { return File.Exists("/proc/loadavg") ? File.ReadAllText("/proc/loadavg").Trim() : "未查到（非 Linux）"; }
        catch { return "未查到"; }
    }

    /// <summary>线程安全、逐行 flush 的证据档写手（同时当 <see cref="IProgress{T}"/> 用，调用线程上当场写）。</summary>
    internal sealed class Sink : IProgress<string>, IDisposable
    {
        private readonly StreamWriter _w;
        private readonly object _lk = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public Sink(string path) { _w = new StreamWriter(path, append: true, new System.Text.UTF8Encoding(false)) { AutoFlush = true }; }
        public void W(string s) { lock (_lk) _w.WriteLine(s); }
        public void Report(string value)
        {
            lock (_lk) _w.WriteLine(value.StartsWith("　　[", StringComparison.Ordinal)
                ? $"{value}　（已用 {_clock.Elapsed.TotalMinutes:0.0} 分）"
                : value);
        }
        public void Dispose() { lock (_lk) _w.Dispose(); }
    }

    [Theory]
    [InlineData("W08")]
    [InlineData("W06")]
    public void 搜形状_Core驱动_整夜实跑(string which)
    {
        var seed = R48NMeshGateTests.Design(which);
        var baseIn = new DesignInputs();
        double lb = GridStart(seed, baseIn);
        var (maxDisc, src) = MaxDisc(lb);
        var dflt = new ShapeSearchOptions();
        string? wf = Environment.GetEnvironmentVariable("SHAPE_WFRAC");
        var opt = new ShapeSearchOptions
        {
            MaxDiscMm = maxDisc, MaxDiscSource = src,
            Lanes = EnvI("SHAPE_LANES", 2),
            ScreenRounds = EnvI("SHAPE_SCREEN", dflt.ScreenRounds),
            FinalRounds = EnvI("SHAPE_FINAL", 40),
            ScreenCoarseMm = EnvD("SHAPE_COARSE", 0),
            EvalSeedFirst = EnvI("SHAPE_SEEDFIRST", 1) != 0,
            MaxExtend = EnvI("SHAPE_MAXEXTEND", 6),
            ParallelFirstPass = EnvI("SHAPE_PARALLEL", dflt.ParallelFirstPass ? 1 : 0) != 0,
            ScreenSkipRadiusGrowth = EnvI("SHAPE_SKIPGROW", dflt.ScreenSkipRadiusGrowth ? 1 : 0) != 0,
            ScreenReuseSameState = EnvI("SHAPE_REUSE", dflt.ScreenReuseSameState ? 1 : 0) != 0,
            ShoulderJPrescreen = EnvI("SHAPE_SHOULDER", dflt.ShoulderJPrescreen ? 1 : 0) != 0,
            WFrac = string.IsNullOrWhiteSpace(wf) ? dflt.WFrac
                  : wf.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray(),
            AllowTabCuts = false,
        };
        string path = DeliverableOut.Stamped($"R48_搜形状_Core驱动_{which}.txt");
        using var sink = new Sink(path);
        var sw = Stopwatch.StartNew();
        sink.W($"# 搜形状 Core 驱动实跑　{which}　（Linux、待 Windows 重录；只印不判）");
        sink.W($"开跑　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　平台 {RuntimeInformation.OSDescription}／{RuntimeInformation.FrameworkDescription}／{Environment.ProcessorCount} 核");
        sink.W($"树　分支 {Git("rev-parse --abbrev-ref HEAD")}　提交 {Git("rev-parse --short HEAD")}　未提交改动 {(Git("status --porcelain -- Pt_Optimize Pt_Optimize.Tests") is var st && st != "未查到" ? "有（本跑用的是工作树里的源码）" : "无")}");
        sink.W($"争用　开跑时 loadavg {LoadAvg()}　同机并跑申报：{Environment.GetEnvironmentVariable("SHAPE_CONTENTION") ?? "未申报"}");
        sink.W($"种子　{seed.Name}（{seed.Provenance}）；盘半径 {seed.DiscRadiusMm:0.0}、舌半宽 {seed.TabHalfWidthMm:0.0}、管壁 {seed.WallMm:0.00}、内径 {seed.TubeIdMm:0.0}");
        sink.W($"工艺参数　new DesignInputs()（缺省）");
        sink.W($"族　不挖舌孔（AllowTabCuts = false，现役设计的解法设定 = 界面下拉预设，与 R48LEndToEndTests.ProductionOptions 同一份）；挖舌孔族本跑不做");
        sink.W($"上端　盘半径 {maxDisc:0.000} mm；出处：{src}");
        sink.W($"参数　粗筛 {opt.ScreenRounds} 轮、精算 {opt.FinalRounds} 轮、并发 {opt.Lanes} 路、粗筛平坦区网格 {opt.ScreenCoarseMm:0.###} mm、先算基准 {(opt.EvalSeedFirst ? "是" : "否")}、邻域最多 {opt.MaxExtend} 轮；其余照抄界面 SearchOneFamilyAsync");
        sink.W($"粗筛轮数　{opt.ScreenRounds}（缺省 {dflt.ScreenRounds}，选定：依据夜跑 deliverable/R48_搜形状_Core驱动_W08_本次开跑于2026-09-24_001304.txt 与 …_021617.txt；界面 16 轮在 4 核 Linux 不可用；跑过再校）");
        sink.W($"算力选项（2026-09-24，判定与阈值不动；每项有改回）　① {(opt.ParallelFirstPass ? "并行首遍（起点表全部点并行解一遍）" : "界面原算法（SHAPE_PARALLEL=0）")}；"
             + $"粗筛终局复核后{(opt.ScreenSkipRadiusGrowth ? "不放大、不重做（SolverOptions.SkipRadiusGrowthAfterFinalCheck = true；本该放大到的半径印在逐形状行末列）" : "照旧放大重做（SHAPE_SKIPGROW=0）")}；"
             + $"粗筛同状态复用 {(opt.ScreenReuseSameState ? "开（SolverOptions.ReuseSameStateSolves = true）" : "关（SHAPE_REUSE=0）")}；"
             + $"赢家精算不跳过放大、同状态复用 {(opt.FinalReuseSameState ? "开" : "关")}；"
             + $"舌宽比 {{{string.Join(", ", opt.WFrac.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture)))}}}；闭式肩部预筛 {(opt.ShoulderJPrescreen ? "开（估计、只作预筛）" : "关（SHAPE_SHOULDER=0）")}");
        sink.W("");
        ShapeSearchResult? res = null;
        try
        {
            res = ShapeSearchDriver.Run(seed, baseIn, opt, sink, CancellationToken.None);
        }
        catch (Exception ex)
        {
            sink.W($"✗ 驱动抛异常：{ex.GetType().Name}：{ex.Message}");
            sink.W(ex.StackTrace ?? "");
            throw;
        }
        finally
        {
            sink.W("");
            sink.W($"总墙钟 {sw.Elapsed.TotalMinutes:0.0} 分钟；结束时 loadavg {LoadAvg()}");
        }
        sink.W("## 逐形状汇总（按编号）");
        sink.W(ShapeRow.Header);
        foreach (var r in res.Rows) sink.W(r.Line());
        var solvedRows = res.Rows.Where(r => !r.Skipped).ToList();
        if (solvedRows.Count > 0)
        {
            sink.W($"每形状耗时　平均 {solvedRows.Average(r => r.Seconds):0} s、最长 {solvedRows.Max(r => r.Seconds):0} s、最短 {solvedRows.Min(r => r.Seconds):0} s；每形状进度轮平均 {solvedRows.Average(r => r.Rounds):0.0}");
            sink.W($"粗筛场解　合计 {solvedRows.Sum(r => r.Solves)} 次、同状态复用省下 {solvedRows.Sum(r => r.Reuses)} 次；"
                 + $"跳过放大重做的形状 {solvedRows.Count(r => !double.IsNaN(r.SkippedGrowthToMm))} 个；肩部预筛跳过 {res.Rows.Count(r => r.Prescreened)} 个；① 首遍分支：{(res.FirstPassBranch.Length > 0 ? res.FirstPassBranch : "界面原算法")}");
        }
        sink.W("");
        sink.W(res.NoFeasible ? res.NoFeasibleReport : res.SchemeCard);

        Assert.True(res.Rows.Count > 0);
        Assert.True(res.NoFeasible ? res.NoFeasibleReport.Length > 0 : res.SchemeCard.Length > 0);
    }
}
