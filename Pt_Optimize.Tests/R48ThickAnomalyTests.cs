using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// R48 探针（2026-09-14，Opus 5 写）：**抬板厚为什么让管孔净流入变差？**
///
/// ══ 反常
///
/// 实测（管壁 0.8，判决网格，求解器第二遍的候选比价）：板厚抬到上界，
/// 管孔净流入 **−1.693 → −1.742**，**更差**。
/// 而朴素预期相反：板厚↑ ⇒ 截面↑ ⇒ 焦耳热↓ ⇒ 发热↓ ⇒ 余数↑（变好）。
///
/// ══ 为什么值得查清
///
/// 管孔净流入是一条能量账上的**余数**：
/// <code>
///   管孔净流入 = (表面散热 + 夹持流出) − 发热
/// </code>
/// 实测那本账每片是 450 W 量级，余数只有几瓦（占发热 0.5～1.3 %）。
/// 所以「抬板厚让余数变差」必然是**四项里某一项在动**，而且动的方向与朴素预期相反。
/// 查清是哪一项在动，机理就定了 —— 这决定求解器该不该继续把板厚列为这条判据的候选旋钮
/// （现在它是候选，实测却帮倒忙，等于每次都白花几次场解、还多花铂）。
///
/// ══ 做法
///
/// 固定其余全部旋钮，只扫板厚，**逐片打印四项**（发热／表面散热／夹持流出／管孔净流入）
/// 外加残差与当地量。只记录、不判定。
///
/// 判据（这个探针要回答什么）：
///   · 发热随板厚**下降**而净流入仍变差 ⇒ 是散热或夹持那一侧被拖累，看是哪一项；
///   · 发热随板厚**上升** ⇒ 朴素预期本身就错（例如厚度改变了电流分布或触发了别的闭式量重定）；
///   · 四项都几乎不动而净流入在动 ⇒ 这个差值已经淹在数值噪声里，那就不是「机理」而是「算不准」。
/// </summary>
[Trait("速度", "慢")]
public class R48ThickAnomalyTests
{
    private readonly ITestOutputHelper _out;
    public R48ThickAnomalyTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 扫板厚看能量账的哪一项在动()
    {
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        string log = DeliverableOut.Stamped("R48_板厚反常_2026-09-14.txt");
        var sb = new StringBuilder();
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(log, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }

        Say($"R48 探针：抬板厚为什么让管孔净流入变差（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        Say("管壁 0.8，舌保温钉在 4.60/2.10/2.90/7.50（求解器 09-13 解出的那一点），只扫板厚。");
        Say("网格 = 复核第 2 档（0.5 mm，整张一起缩）。恒等式：管孔净流入 = (表面散热 + 夹持流出) − 发热。");
        Say("");

        var p = new DesignInputs();
        var (_, radius) = MeshVerify.RequiredMeshFor(DesignSpec.W08.Clone(), p);
        double[] baseThick = { 0.73, 1.26, 1.26, 0.73 };

        foreach (double mul in new[] { 1.00, 1.30, 1.70 })
        {
            var d = DesignSpec.W08.Clone();
            d.TabThickMm = baseThick.Select(v => Math.Round(v * mul, 2)).ToArray();
            d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
            d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
            d = d.Fit();
            d.SizeTongues(p);

            var lc = d.BuildCase(p, checkRamp: false);
            MeshAdapt.RefineWholeMesh(lc, 0.5, radius);
            var sw = Stopwatch.StartNew();
            double last = 0;
            var tick = new Progress<string>(m =>
            {
                if (sw.Elapsed.TotalSeconds - last < 45) return;
                last = sw.Elapsed.TotalSeconds;
                Say($"     · ×{mul:0.00}　[已跑 {sw.Elapsed.TotalSeconds:0} s] {m}");
            });
            LineResult r;
            try { r = LineRunner.Run(lc, tick, default); }
            catch (Exception ex) { Say($"×{mul:0.00}　解不出来：{ex.Message}"); continue; }
            sw.Stop();
            if (!r.Ok) { Say($"×{mul:0.00}　解不出来：{r.Message}"); continue; }

            double V(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
            Say($"── 板厚 ×{mul:0.00}　= {string.Join("/", d.TabThickMm.Select(v => v.ToString("0.00")))} mm"
              + $"　舌片厚 {string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.00")))} mm（闭式）"
              + $"　合计 {r.TotalMassG:0} g　用时 {sw.Elapsed.TotalSeconds:0} s");
            Say($"   判据：管孔净流入 {V(LineResult.Key.NetFlux):+0.000;-0.000}　"
              + $"增量温降 {V(LineResult.Key.FlangeDip):0.000}　圆盘区 {V(LineResult.Key.DiscTemp):0.000}　"
              + $"截面J {V(LineResult.Key.SectionJ):0.00}");
            Say($"   {"片",3}{"发热W",10}{"表面散热W",12}{"夹持流出W",12}{"净流入W",11}{"残差W",10}{"舌片区峰C",11}{"管根C",9}");
            foreach (var (f, j) in r.Flanges.Select((f, jj) => (f, jj)))
                Say($"   {j,3}{f.QGenW,10:0.0}{f.QLossW,12:0.0}{f.QClampW,12:0.0}"
                  + $"{f.QFromTubeW,11:+0.000;-0.000}{f.EnergyResidualW,10:+0.000;-0.000}"
                  + $"{f.TTabMaxC,11:0.0}{f.TRootC,9:0.0}");
            Say("");
        }

        Say("读法：对比三档的**同一片**，看四项里哪一项随板厚变得最多。");
        Say("  · 发热↓ 而 散热↓ 更多 ⇒ 加厚把热更多地导给了别处（例如经圆盘桥给管子），散热面没跟上；");
        Say("  · 发热↑ ⇒ 朴素预期（加厚必然少发热）在这个几何上不成立，要看电流分布；");
        Say("  · 四项都几乎不动 ⇒ 净流入那点差异是数值噪声，不是机理。");

        Assert.True(sb.ToString().Split('\n').Count(l => l.Contains("板厚 ×")) >= 2,
            "至少要扫出两档才有对照 —— 仪器空转了，别拿它下结论");
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
