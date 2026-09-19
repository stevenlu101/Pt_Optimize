using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 探针四重做（2026-09-14，Opus 5 写；规格出自常驻数值把关人第九、十轮）：
/// **外层耦合容差 1.0 / 0.5 / 0.25 K 阶梯 —— 耦合停机噪声实测，放在任何保温格点搜索之前。**
///
/// ══ 为什么现在跑
/// 保温变成 0.5 mm 取整的求解变量后，判定改为格点三态（离限值 ≥ 3U 过／反向超 3U 不过／其余判不了），
/// 粗筛门槛与网格阶梯外推都要把耦合噪声算进去；外推还要求单次解噪声远小于最小步长（0.5→0.25 mm 那步只有 0.3～0.7 W）。
/// 单片抽热对管根灵敏度 +0.17～+0.36 W/K（R48_管根接线与灵敏度_2026-09-14.txt），耦合剩余 0.5～0.9 K ⇒ 单次噪声估计 0.1～0.3 W，要实测。
/// ⚠ 上一版探针四（1.0 vs 0.05 K）设计有病（未收敛的也读、0.05 K 低于内层精度地板），已停跑作废。
///
/// ══ 设置
/// 管壁 0.8 落点（舌保温 4.6/2.1/2.9/7.5、圆盘保温取算例值 20 mm），保温按半径 + 分界格混合 + 压接锚点（当前生产配方，细带未开），
/// 0.5 mm 自相似、细区半径按 RequiredMeshFor。三个容差各一个 Fact（可并行三个进程），每个写一份机读行文件；第四个 Fact 读三份文件判读。
///
/// ══ 跑前写死的判读
///   · 每档都断言外层耦合收敛；没收敛的那档不读（判读 Fact 报「0.25 K 收不敛 ⇒ 耦合容差地板 0.5 K，外推的 U 要计入这部分噪声」）。
///   · 0.5 → 0.25 K 各量变化都小于阈值 ⇒ **0.5 K 够用**；阈值 = 0.1 × 对应最小步长：
///       逐片抽热 0.03 W（最小步长 0.3 W，R48_离散误差预算 细带/均匀组 0.5→0.25）；
///       逐接头增量温降 0.07 K（0.3 W × 2.4 K/W × 0.1）；
///       逐片圆盘区−管温 0.05 K（圆盘区网格步长在 0.5→0.25 已小到 0.01～0.03 K，0.1 倍无意义；改取粗筛门槛余量 0.5 K 的 0.1 倍）。
///   · 否则 ⇒ 0.5 K 不够，报各量最大变化与 1.0→0.5 的比较，供数值把关人定容差。
/// </summary>
[Trait("速度", "慢")]
public class R48CoupleTolLadderTests
{
    private readonly ITestOutputHelper _out;
    public R48CoupleTolLadderTests(ITestOutputHelper o) { _out = o; }

    // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）；判读读回每档最新一跑（DeliverableOut.Latest，没有带时刻的就退回原文件名）
    private static string NameFor(double tol) => $"R48_耦合容差阶梯_{tol.ToString("0.00", CultureInfo.InvariantCulture)}K_2026-09-14.txt";
    private static string FileFor(double tol) => DeliverableOut.Stamped(NameFor(tol));

    [Fact] public void 耦合容差_1K() => Run(1.0);
    [Fact] public void 耦合容差_0点5K() => Run(0.5);
    [Fact] public void 耦合容差_0点25K() => Run(0.25);

    private void Run(double tol)
    {
        var sb = new StringBuilder();
        string file = FileFor(tol);
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        d.SizeTongues(p);
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        var lc = d.BuildCase(p, checkRamp: false);
        MeshAdapt.RefineWholeMesh(lc, 0.5, radius);
        lc.CoupleTolK = tol;
        if (tol < 0.9) lc.CoupleMaxRounds = Math.Max(lc.CoupleMaxRounds, 4000);
        Say($"R48 耦合容差阶梯（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　容差 {tol:0.00} K　最多 {lc.CoupleMaxRounds} 轮　0.5 mm 细区半径 {radius:0}　圆盘保温 {lc.Base.FlangeInsulThickMm:0.0} mm");
        var sw = System.Diagnostics.Stopwatch.StartNew(); double last = 0;
        var tick = new Progress<string>(m =>
        {
            if (sw.Elapsed.TotalSeconds - last < 120) return;
            last = sw.Elapsed.TotalSeconds;
            Say($"     · [已跑 {sw.Elapsed.TotalSeconds:0} s] {m}");
        });
        var r = LineRunner.Run(lc, tick, default);
        Assert.True(r.Ok, "整线解不出来：" + r.Message);
        Say($"用时 {sw.Elapsed.TotalMinutes:0.0} 分　收敛 {r.Converged}　耦合剩余估计 {r.CoupleRemainK:0.000} K");
        var inv = CultureInfo.InvariantCulture;
        // 机读行：DATA|converged|remainK|q0..q3|disc0..disc3|dipA0,dipB0,dipA1,dipB1,dipA2,dipB2
        string line = "DATA|" + (r.Converged ? "1" : "0") + "|" + r.CoupleRemainK.ToString("R", inv)
            + "|" + string.Join(",", r.Flanges.Select(f => f.QFromTubeW.ToString("R", inv)))
            + "|" + string.Join(",", r.Flanges.Select(f => (f.TDiscMaxC - f.TRootC).ToString("R", inv)))
            + "|" + string.Join(",", r.Segments.SelectMany(s => new[] { s.FlangeDipAK, s.FlangeDipBK }).Select(v => v.ToString("R", inv)));
        Say($"逐片抽热 {string.Join(" / ", r.Flanges.Select(f => f.QFromTubeW.ToString("+0.0000;-0.0000")))} W");
        Say($"逐片圆盘区−管温 {string.Join(" / ", r.Flanges.Select(f => (f.TDiscMaxC - f.TRootC).ToString("+0.0000;-0.0000")))} K");
        Say($"逐段增量温降 A/B {string.Join("　", r.Segments.Select(s => $"{s.FlangeDipAK:+0.0000;-0.0000}/{s.FlangeDipBK:+0.0000;-0.0000}"))} K");
        Say(line);
        Assert.True(r.Converged, $"容差 {tol} K 外层耦合没收敛 —— 本档不读");
    }

    [Fact]
    public void 判读_三档都跑完后()
    {
        var sb = new StringBuilder();
        string file = DeliverableOut.Stamped("R48_耦合容差阶梯_判读_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        var inv = CultureInfo.InvariantCulture;
        (bool conv, double remain, double[] q, double[] disc, double[] dip)? Read(double tol)
        {
            string f = DeliverableOut.Latest(NameFor(tol));
            if (!File.Exists(f)) return null;
            var l = File.ReadAllLines(f).LastOrDefault(x => x.StartsWith("DATA|", StringComparison.Ordinal));
            if (l is null) return null;
            var parts = l.Split('|');
            double[] Arr(string s) => s.Split(',').Select(v => double.Parse(v, inv)).ToArray();
            return (parts[1] == "1", double.Parse(parts[2], inv), Arr(parts[3]), Arr(parts[4]), Arr(parts[5]));
        }
        Say($"R48 耦合容差阶梯判读（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        var r1 = Read(1.0); var r05 = Read(0.5); var r025 = Read(0.25);
        Assert.True(r1 is not null && r05 is not null && r025 is not null, "三档输出文件没齐（先跑三个容差 Fact）");
        foreach (var (tol, rr) in new[] { (1.0, r1!.Value), (0.5, r05!.Value), (0.25, r025!.Value) })
            Say($"   容差 {tol:0.00} K：收敛 {rr.conv}　剩余估计 {rr.remain:0.000} K");
        if (!r025!.Value.conv)
        {
            Say("★ 0.25 K 收不敛 ⇒ 耦合容差地板 0.5 K，网格阶梯外推的 U 要计入 0.5 K 档的耦合噪声。");
            return;
        }
        if (!r05!.Value.conv || !r1!.Value.conv) { Say("★ **0.5 或 1.0 K 档没收敛 —— 这组数不读，先查**"); Assert.Fail("有档没收敛"); }
        double MaxDiff(double[] a, double[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Max();
        double dq105 = MaxDiff(r1.Value.q, r05.Value.q), dq0525 = MaxDiff(r05.Value.q, r025.Value.q);
        double dd105 = MaxDiff(r1.Value.disc, r05.Value.disc), dd0525 = MaxDiff(r05.Value.disc, r025.Value.disc);
        double dp105 = MaxDiff(r1.Value.dip, r05.Value.dip), dp0525 = MaxDiff(r05.Value.dip, r025.Value.dip);
        Say($"   逐片抽热最大变化：1.0→0.5 {dq105:0.0000} W　0.5→0.25 {dq0525:0.0000} W（阈值 0.03）");
        Say($"   逐片圆盘区最大变化：1.0→0.5 {dd105:0.0000} K　0.5→0.25 {dd0525:0.0000} K（阈值 0.05）");
        Say($"   逐接头增量温降最大变化：1.0→0.5 {dp105:0.0000} K　0.5→0.25 {dp0525:0.0000} K（阈值 0.07）");
        bool enough = dq0525 < 0.03 && dd0525 < 0.05 && dp0525 < 0.07;
        Say(enough ? "★ 0.5 → 0.25 K 各量变化都小于阈值 ⇒ 耦合容差 0.5 K 够用。"
                   : "★ **0.5 → 0.25 K 有量超过阈值 ⇒ 0.5 K 不够**，交数值把关人定容差。");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
