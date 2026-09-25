using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 搜形状检查点续跑（2026-09-25，<see cref="ShapeSearchCheckpoint"/>）：
/// 门 a 同钥匙第二跑不再解、行逐位相同、只有精算（FineMm &gt; 0）还调求解；b 代码戳不同 ⇒ 全部重解、零命中；
/// c 坏行只跳过并计数；d 改回（不给路径）不读不写；e 钥匙对设计／求解选项／工艺参数／代码戳都敏感。
/// 假求解，微秒级。不覆盖：真求解器的逐位（同代码同输入求解器确定，落盘字段原样抄回；实跑档头与「续跑复用」行可核）。
/// </summary>
public class R48ShapeSearchCheckpointTests
{
    private static Func<DesignSpec, DesignInputs, SolverOptions, IProgress<string>?, CancellationToken, SolverResult> Fake(
        double feasFrom, List<(double R, double hw, bool taper, double fine)> calls)
        => (d, b, o, p, t) =>
        {
            lock (calls) calls.Add((d.DiscRadiusMm, d.TabHalfWidthMm, d.TabTaper, o.FineMm));
            var dd = d.Clone();
            for (int j = 0; j < dd.TabThickMm.Length; j++) dd.TabThickMm[j] = 1.0 + 0.01 * d.DiscRadiusMm;
            bool ok = d.DiscRadiusMm >= feasFrom - 1e-9;
            double hot = ok ? 1.0 : 5.0 + 100.0 / d.DiscRadiusMm;
            return new SolverResult
            {
                Design = dd, Feasible = ok,
                MassG = 1000 + 20 * d.DiscRadiusMm + 3 * (d.DiscRadiusMm - d.TabHalfWidthMm) + (d.TabTaper ? 7 : 0),
                Solves = 3, SameStateReuses = 1, HitBound = !ok, StopWhy = ok ? "" : $"假停因 R={d.DiscRadiusMm:0.000}", Message = ok ? "假：全过" : "假：不可行",
                Best = new LineResult
                {
                    Ok = true, Converged = true, RampChecked = true,
                    Checks = new[]
                    {
                        new ConstraintOut { Name = LineResult.Key.HotOverContact, Unit = "K", Actual = hot, Limit = 10, LessIsBetter = true, Ok = hot <= 10, Kind = CheckKind.HardSafety, Where = "入口" },
                        new ConstraintOut { Name = LineResult.Key.TubeToFlangeHeat, Unit = "W", Actual = -1, Limit = 0, LessIsBetter = true, Ok = true, Kind = CheckKind.HardSafety },
                        new ConstraintOut { Name = LineResult.Key.LocalStab, Unit = "×", Actual = 2, Limit = 1, LessIsBetter = false, Ok = true, Kind = CheckKind.HardSafety },
                    },
                },
                FineRefined = o.FineMm > 0, FineMmUsed = o.FineMm,
                DesignCurrent = new DesignCurrent.Result { PlateA = Enumerable.Repeat(600.0, dd.TabThickMm.Length).ToArray() },
            };
        };

    private static (DesignSpec seed, DesignInputs b, double lb) Seed()
    {
        var seed = R48NMeshGateTests.Design("W08");
        var b = new DesignInputs();
        return (seed, b, R48ShapeSearchRunTests.GridStart(seed, b));
    }

    private static ShapeSearchOptions Opt(double lb, string? path, string stamp, List<(double R, double hw, bool taper, double fine)> calls) => new()
    {
        MaxDiscMm = lb + 12, MaxDiscSource = "门用", EvalSeedFirst = false, Lanes = 1, MaxExtend = 1,
        CheckpointPath = path, CheckpointStamp = stamp, SolveOverride = Fake(lb + 5, calls),
    };

    private static string Row(ShapeRow r) => string.Join("|", r.Phase, r.R.ToString("R"), r.HalfW.ToString("R"), r.Taper, r.Skipped, r.Ok,
        r.MassG.ToString("R"), r.Message, r.StopWhy, r.HitBound, r.Undetermined, string.Join(",", r.Blocked),
        r.Hot.Actual.ToString("R"), r.Cold.Actual.ToString("R"), r.Flux.Actual.ToString("R"), r.Solves, r.Reuses,
        r.Design is null ? "-" : string.Join(",", r.Design.TabThickMm.Select(v => v.ToString("R"))));

    [Fact]
    public void 门a_同钥匙第二跑不再解_行逐位相同_只剩精算调求解()
    {
        var (seed, b, lb) = Seed();
        string dir = Path.Combine(Path.GetTempPath(), "ckpt_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "sub", "ckpt.jsonl");
        try
        {
            var c1 = new List<(double R, double hw, bool taper, double fine)>();
            var r1 = ShapeSearchDriver.Run(seed, b, Opt(lb, path, "门戳", c1), null, CancellationToken.None);
            Assert.True(File.Exists(path));
            Assert.Equal(0, r1.CheckpointHits);
            int nav1 = c1.Count(c => c.fine == 0), fine1 = c1.Count(c => c.fine > 0);
            Assert.True(nav1 >= 3 && fine1 == 1, $"nav {nav1} fine {fine1}");
            Assert.Equal(nav1, r1.CheckpointMisses);
            Assert.Equal(nav1, File.ReadAllLines(path).Count(l => l.Length > 0));

            var c2 = new List<(double R, double hw, bool taper, double fine)>();
            var r2 = ShapeSearchDriver.Run(seed, b, Opt(lb, path, "门戳", c2), null, CancellationToken.None);
            Assert.Equal(0, c2.Count(c => c.fine == 0));          // 导航网格那一遍一次都不解
            Assert.Equal(1, c2.Count(c => c.fine > 0));           // 精算照常重解
            Assert.Equal(nav1, r2.CheckpointHits);
            Assert.Equal(0, r2.CheckpointMisses);
            Assert.Equal(nav1, r2.CheckpointLoaded);
            Assert.Contains(r2.Log, l => l.Contains("续跑复用：", StringComparison.Ordinal));
            Assert.Contains(r2.Log, l => l.Contains("检查点小结：续跑复用", StringComparison.Ordinal));
            // 行逐位相同（阶段、形状、可行、铂重、消息、卡住的判据、三条硬判据值、板厚）
            Assert.Equal(r1.Rows.Count, r2.Rows.Count);
            for (int i = 0; i < r1.Rows.Count; i++) Assert.Equal(Row(r1.Rows[i]), Row(r2.Rows[i]));
            Assert.Equal(r1.Winner?.Tag, r2.Winner?.Tag);
            Assert.Equal(r1.Final?.MassG, r2.Final?.MassG);
            Assert.Equal(nav1, File.ReadAllLines(path).Count(l => l.Length > 0));   // 复用不重复落盘
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 门b_代码戳不同_零命中全部重解()
    {
        var (seed, b, lb) = Seed();
        string path = Path.Combine(Path.GetTempPath(), "ckpt_" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var c1 = new List<(double R, double hw, bool taper, double fine)>();
            ShapeSearchDriver.Run(seed, b, Opt(lb, path, "abc1234", c1), null, CancellationToken.None);
            var c2 = new List<(double R, double hw, bool taper, double fine)>();
            var r2 = ShapeSearchDriver.Run(seed, b, Opt(lb, path, "abc1234+dirty:0123456789ab", c2), null, CancellationToken.None);
            Assert.Equal(c1.Count, c2.Count);
            Assert.Equal(0, r2.CheckpointHits);
            Assert.Equal(c1.Count(c => c.fine == 0), r2.CheckpointMisses);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 门c_坏行只跳过并计数()
    {
        var (seed, b, lb) = Seed();
        string path = Path.Combine(Path.GetTempPath(), "ckpt_" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var c1 = new List<(double R, double hw, bool taper, double fine)>();
            ShapeSearchDriver.Run(seed, b, Opt(lb, path, "门戳", c1), null, CancellationToken.None);
            File.AppendAllText(path, "{ 这不是 json\n\n{\"Key\":\"\"}\n");
            var st = new ShapeSearchCheckpoint.Store(path, "门戳");
            Assert.Equal(c1.Count(c => c.fine == 0), st.Loaded);
            Assert.Equal(2, st.BadLines);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 门d_改回_不给路径_不读不写()
    {
        var (seed, b, lb) = Seed();
        var c1 = new List<(double R, double hw, bool taper, double fine)>();
        var r1 = ShapeSearchDriver.Run(seed, b, Opt(lb, null, "门戳", c1), null, CancellationToken.None);
        Assert.Equal(0, r1.CheckpointLoaded + r1.CheckpointHits + r1.CheckpointMisses);
        Assert.DoesNotContain(r1.Log, l => l.Contains("检查点", StringComparison.Ordinal));
        Assert.Null(new ShapeSearchOptions().CheckpointPath);
    }

    [Fact]
    public void 门e_钥匙对设计_求解选项_工艺参数_代码戳都敏感()
    {
        var (seed, b, _) = Seed();
        var o = new SolverOptions { MaxRounds = 8 };
        string k0 = ShapeSearchCheckpoint.KeyOf(seed, b, o, "s");
        Assert.Equal(k0, ShapeSearchCheckpoint.KeyOf(seed.Clone(), new DesignInputs(), new SolverOptions { MaxRounds = 8 }, "s"));
        var d2 = seed.Clone(); d2.DiscRadiusMm += 0.5;
        Assert.NotEqual(k0, ShapeSearchCheckpoint.KeyOf(d2, b, o, "s"));
        Assert.NotEqual(k0, ShapeSearchCheckpoint.KeyOf(seed, b, new SolverOptions { MaxRounds = 9 }, "s"));
        Assert.NotEqual(k0, ShapeSearchCheckpoint.KeyOf(seed, new DesignInputs { DiscInsulCapMm = 9 }, o, "s"));
        Assert.NotEqual(k0, ShapeSearchCheckpoint.KeyOf(seed, b, o, "t"));
        Assert.Equal(64, k0.Length);
    }
}
