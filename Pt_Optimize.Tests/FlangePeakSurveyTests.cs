using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ 仪器（2026-09-08，R18 动代码之前先量）：0.8 档在求解器解出的旋钮上，**整片**（含舌片）峰值温度离管温多远。
///
/// 为什么要先量：R18 打算把参考行「法兰最高温 − 管温（整片，含舌片）」升为硬判据（限 5 K，与 ②″ 同源），
/// 把「法兰 J ≤ 10」这个烧断的代理量降为参考。升之前必须知道现役档会不会被它误判 —— 不量就改，
/// 就是拿一条没验过的判据去判已交付的东西。
///
/// 旋钮取自 deliverable/对帐超时_轨迹.txt（2026-09-08，导航网格）：板厚 0.83/2.32/2.16/0.72、舌保温 0.30、t₁ 1.00、t₂ 1/1.01/1/1。
/// 只解一次（导航网格），不判、不改，落档 deliverable/整片峰值_0.8档.txt。
/// </summary>
public class FlangePeakSurveyTests
{
    [Trait("速度", "慢")]
    [Fact]
    public void 量0点8档整片峰值()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        double[] t = { 0.83, 2.32, 2.16, 0.72 };
        double[] t2 = { 1.00, 1.01, 1.00, 1.00 };
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            d.TabThickMm[j] = t[j]; d.TabInsulMm[j] = 0.30; d.RingMul[j] = 1.00; d.RingMul2[j] = t2[j];
        }
        var r = LineRunner.Run(d.BuildCase(p, checkRamp: false), null, default);

        var sb = new StringBuilder();
        sb.AppendLine("═══ 0.8 档（求解器解出的旋钮，导航网格）整片峰值 vs 管温 ═══");
        sb.AppendLine($"Ok={r.Ok}　Converged={r.Converged}　OverMelt={r.OverMelt}　合计 {r.TotalMassG:0.0} g");
        sb.AppendLine("片\t管根°C\t整片峰值°C\t圆盘峰值°C\t舌片峰值°C\t整片−管根 K\t圆盘−管根 K\tJ_max A/mm²\t铂 g");
        foreach (var f in r.Flanges)
            sb.AppendLine($"{f.Name}\t{f.TRootC:0.0}\t{f.TMaxC:0.0}\t{f.TDiscMaxC:0.0}\t{f.TTabMaxC:0.0}\t"
                        + $"{f.TMaxC - f.TRootC:+0.00;-0.00}\t{f.TDiscMaxC - f.TRootC:+0.00;-0.00}\t{f.JMaxAPerMm2:0.00}\t{f.MassG:0}");
        sb.AppendLine();
        sb.AppendLine("判据表（原样）：");
        foreach (var c in r.Checks)
            sb.AppendLine($"  {c.Name}\t实际 {c.Actual:0.000}\t限 {c.Limit:0.###}\t{(c.Undetermined ? "判不了" : c.Ok ? "过" : "不过")}\t{c.Kind}\t{c.Where}");
        Directory.CreateDirectory(Path.Combine(HandoverDoc.Root(), "deliverable"));
        File.WriteAllText(Path.Combine(HandoverDoc.Root(), "deliverable", "整片峰值_0.8档.txt"), sb.ToString());
        Console.WriteLine(sb.ToString());

        Assert.True(r.Ok && r.Converged, $"这一次解没解出来（Ok={r.Ok} Converged={r.Converged}）：{r.Message}");
    }
}
