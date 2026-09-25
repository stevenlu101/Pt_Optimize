using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 探针（2026-09-14，Opus 5 写；物理把关人第三轮：「圆盘保温要在重解之前处理」）：
/// **圆盘保温厚度这根一直冻着的杠杆，对三条法兰判据怎么走？**
///
/// ══ 起因
/// 整线算例的圆盘保温取 `DesignSpec.FlangeInsulMm = 20`（BuildCase：`p.FlangeInsulThickMm = FlangeInsulated ? FlangeInsulMm : 0`）。
/// 物理把关人查仓库：这个 20 **没有出处**——最早出现在 2026-08-12 一组扫描设置里，同组的管保温 10、壁厚 0.4、夹持 300 后来都改了，只有它留下；
/// 而 `DesignInputs.cs` 2026-08-10 那段专门推过取 2.5 mm（待现场确认），并预言「法兰包厚、管子包薄会向管根倒灌」；
/// `HANDOVER.md` 记着已确认的回答：「法兰保温可以完全不包，全依计算需求，是 0 起的自由变量」。
/// 保温按半径划之后，保温规则 AB 对照显示光是 −x 半个圆盘换保温，每片抽热就变 7～25 W —— 它是三条法兰判据共同的主导边界条件。
///
/// ⚠ 上一版探针三的「杠杆二」改的是 `DesignInputs.FlangeInsulThickMm`，被 BuildCase 用 20 覆盖，**空转**（数据逐位相同为证）。
///   本探针改的是 **`DesignSpec.FlangeInsulMm`**，并逐行打印算例里**实际**用的值，接线错了当场看得出。
///
/// 设置：管壁 0.8 落点、保温按半径、0.5 mm 自相似、细区半径 59。每行打判据值与取自哪片、逐片抽热、
/// 逐片圆盘区−管温与峰位、舌片厚与合计铂重（核「其余钉住」—— 物理把关人第 4 条）。只记录，不判定：
/// 这是设计输入的取值问题，给数，结论等两位把关人看过数再说。
/// ⚠ 一维保温模型在 20 mm 包 4.2 mm 宽圆环时不成立（物理把关人）：它低估圆盘散热，厚保温那几行偏保守。
/// </summary>
[Trait("速度", "慢")]
public class R48DiscInsulLeverTests
{
    private readonly ITestOutputHelper _out;
    public R48DiscInsulLeverTests(ITestOutputHelper o) { _out = o; }

    [Fact] public void 圆盘保温_2点5mm() => Run(2.5);
    [Fact] public void 圆盘保温_5mm() => Run(5.0);
    [Fact] public void 圆盘保温_10mm() => Run(10.0);
    [Fact] public void 圆盘保温_20mm() => Run(20.0);

    private void Run(double discInsulMm)
    {
        var sb = new StringBuilder();
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        string file = DeliverableOut.Stamped($"R48_圆盘保温杠杆_{discInsulMm:0.0}mm_2026-09-14.txt");
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
        d.FlangeInsulMm = discInsulMm;
        d = d.Fit();
        d.SizeTongues(p);
        var (_, radius) = MeshVerify.RequiredMeshFor(d, p);
        var lc = d.BuildCase(p, checkRamp: false);
        MeshAdapt.RefineWholeMesh(lc, 0.5, radius);

        Say($"R48 圆盘保温杠杆（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　管壁 0.8 落点，保温按半径，0.5 mm，细区半径 {radius:0}");
        Say($"设定 DesignSpec.FlangeInsulMm = {discInsulMm:0.0}；算例实际圆盘保温 {lc.Base.FlangeInsulThickMm:0.0} mm"
          + (Math.Abs(lc.Base.FlangeInsulThickMm - discInsulMm) > 1e-9 ? "　⚠⚠ **与设定不符 —— 接线没接上，本行数据作废**" : ""));
        Say($"舌片厚 {string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.00")))} mm　舌保温 {string.Join("/", d.TabInsulMm.Select(v => v.ToString("0.0")))} mm");

        var sw = System.Diagnostics.Stopwatch.StartNew(); double last = 0;
        var tick = new Progress<string>(m =>
        {
            if (sw.Elapsed.TotalSeconds - last < 90) return;
            last = sw.Elapsed.TotalSeconds;
            Say($"     · [已跑 {sw.Elapsed.TotalSeconds:0} s] {m}");
        });
        var r = LineRunner.Run(lc, tick, default);
        Assert.True(r.Ok, "整线解不出来：" + r.Message);

        double V(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
        string W(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Where ?? "";
        Say($"用时 {sw.Elapsed.TotalMinutes:0.0} 分　外层耦合收敛 {r.Converged}　耦合剩余估计 {r.CoupleRemainK:0.00} K　{r.MeshCells} 单元　合计铂重 {r.TotalMassG:0} g");
        Say($"判据：管孔净流入 {V(LineResult.Key.NetFlux):+0.000;-0.000} W（{W(LineResult.Key.NetFlux)}）"
          + $"　圆盘区最高温 {V(LineResult.Key.DiscTemp):+0.000;-0.000} K（{W(LineResult.Key.DiscTemp)}）"
          + $"　法兰增量温降 {V(LineResult.Key.FlangeDip):+0.000;-0.000} K（{W(LineResult.Key.FlangeDip)}）"
          + $"　截面 J {V(LineResult.Key.SectionJ):0.00}　全过 {r.AllOk}");
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            var f = r.Flanges[j];
            Say($"   片{j}　抽热 {f.QFromTubeW,8:+0.000;-0.000} W　管根 {f.TRootC,8:0.00} °C　圆盘区−管温 {f.TDiscMaxC - f.TRootC,7:+0.000;-0.000} K"
              + $"（峰位 r={f.DiscMaxRMm:0.0} x={f.DiscMaxXMm:+0.0;-0.0}）　舌区峰 {f.TTabMaxC - f.TRootC:+0.00;-0.00} K（r={f.TabMaxRMm:0.0}）"
              + $"　{f.InsulRule}");
        }
        for (int i = 0; i < r.Segments.Length; i++)
            Say($"   段{i}　增量温降 A 端 {r.Segments[i].FlangeDipAK:+0.000;-0.000} K　B 端 {r.Segments[i].FlangeDipBK:+0.000;-0.000} K");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
