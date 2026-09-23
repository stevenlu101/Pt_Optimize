using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 第一轮保温扫描（2026-09-14，Opus 5 写；规格、预测与读法出自物理把关人第七轮）：
/// **管保温 T 能不能把「圆盘区温度」和「抽热窗口」拆开？**
///
/// ══ 起因
/// 用户 2026-09-14：管、圆盘、舌片保温都是开放边界条件，由 APP 算，每层 0.5 mm；保温是给法兰与管壁减厚的手段；先能造能用，再比铂重。
/// 方案 A（R48_方案A_X*）表明只动法兰上的保温（管保温钉 5 mm）解不了入口片；物理把关人：盘、舌保温只改散热，会让抽热与舌根温度按固定比例一起动，
/// **只有管保温改电流（法兰焦耳热 ∝ I²）**，方向上能把两件事拆开。
///
/// ══ 五档（0.5 mm 整线、生产配方：保温按半径 + 分界格混合 + 压接锚点；舌片厚按各档设计电流 SizeTongues 重算；管壁 0.8 固定）
///   R0 T=5  D=W=5（参照，与方案 A X=5 同设定，本批同一可执行重跑，以免跨批比较）
///   R1 T=7.5 D=W=5　　R2 T=10 D=W=5　　R4 T=5 D=7.5 W=5　　R5 T=5 D=W=5，保温物性整体 ×1.1（LossScale = 1.1）
///
/// ══ 跑前登记的预测（物理把关人，推理）：R1 相对 R0
///   段电流 −5～−10 %；片0 抽热 +11～+21 W；片0 圆盘区温度 −5～−10 K；各接头 K/W 仍在 2.2～2.6；舌片厚 −5～−10 %。
///   证伪：片0 圆盘区温度降得 &lt; 2 K 或反而升 ⇒ 管保温拆不开两件事，只能回到形状；K/W 变化超 ±20 % ⇒ K/W 估算错。
///
/// ══ 读法
///   · 逐片读；判管保温有没有用只看端片（片0、片3），共用片等双扣开关与耦合容差定了再判（耦合容差阶梯：共用片判据在 1.0 K 容差下漂 0.2 W / 0.5 K 级）。
///   · 打印绝对温度（设定、段中点、管根、盘峰、舌区峰）与「盘峰 − 设定」「舌区峰 − 设定」，以便圆盘区判据的基准无论最后怎么定都不必重跑
///     （物理把关人第七轮第 (4) 条：热偶误差意图下，基准可能应是热偶读数而非接头管根）。
///   · K/W 用「该接头增量温降 ÷ 该片抽热」逐端片估（端片截距约 0）。
/// ⚠ 管段每米功率 P′ 拆成散热/加热玻璃两部分，SegmentOut 里没有，本批不打。
/// </summary>
[Trait("速度", "慢")]
public class R48TubeInsulScanTests
{
    private readonly ITestOutputHelper _out;
    public R48TubeInsulScanTests(ITestOutputHelper o) { _out = o; }

    [Fact] public void R0_管5_盘5_舌5() => Run("R0", 5.0, 5.0, 5.0, 1.0);
    [Fact] public void R1_管7点5_盘5_舌5() => Run("R1", 7.5, 5.0, 5.0, 1.0);
    [Fact] public void R2_管10_盘5_舌5() => Run("R2", 10.0, 5.0, 5.0, 1.0);
    [Fact] public void R4_管5_盘7点5_舌5() => Run("R4", 5.0, 7.5, 5.0, 1.0);
    [Fact] public void R5_管5_盘5_舌5_物性1点1() => Run("R5", 5.0, 5.0, 5.0, 1.1);

    private void Run(string tag, double tube, double disc, double tab, double lossScale)
    {
        var sb = new StringBuilder();
        string file = Path.Combine(Root(), "deliverable", $"R48_第一轮保温扫描_{tag}_2026-09-14.txt");
        // ★ R48 G1（2026-09-15，Opus 5；数值把关人第十三轮）：写文件改走 EvidenceFile —— 文件头写明代码（git HEAD、Core 改动指纹）、配方、耦合容差、共用片抽热与工况；
        //   同名文件已存在且头不同（或是没有头的旧证据）⇒ 不覆盖，改写到带时间戳的新文件。算法一行不动。
        //   证据头要算例，算例建好后才打开（下面 ev 赋值处）；在那之前 Say 只进缓冲，打开时整份写出。
        EvidenceFile? ev = null;
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { ev?.Write(sb.ToString()); } catch { }
        }
        var p = new DesignInputs { LossScale = lossScale };
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.TubeInsulMm = tube;
        d.FlangeInsulMm = disc;
        d.TabInsulMm = new[] { tab, tab, tab, tab };
        d = d.Fit();
        d.SizeTongues(p);
        var (_, radius) = MeshVerify.RequiredMeshFor(d, p);
        var lc = d.BuildCase(p, checkRamp: false);
        MeshAdapt.RefineWholeMesh(lc, 0.5, radius);
        ev = EvidenceFile.Open(file, EvidenceHeader.ForLineCase($"R48 第一轮保温扫描 {tag}", lc));
        _out.WriteLine($"证据文件：{ev.Path}（{ev.Reason}）");
        try { ev.Write(sb.ToString()); } catch { }
        if (ev.Redirected) Say("⚠ 证据文件改道：" + ev.Reason);
        Say($"R48 第一轮保温扫描 {tag}（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　0.5 mm 细区半径 {radius:0}　管壁 {d.WallMm:0.00}");
        Say($"设定 管保温 {tube:0.0}／圆盘 {disc:0.0}／舌 {tab:0.0} mm　物性 ×{lossScale:0.00}；算例实际：管保温层 {lc.Base.Layer1.ThicknessMm:0.0}（启用 {lc.Base.Layer1.Enabled}）"
          + $"　圆盘 {lc.Base.FlangeInsulThickMm:0.0}　物性 ×{lc.Base.LossScale:0.00}"
          + ((Math.Abs(lc.Base.Layer1.ThicknessMm - tube) > 1e-9 || Math.Abs(lc.Base.FlangeInsulThickMm - disc) > 1e-9 || Math.Abs(lc.Base.LossScale - lossScale) > 1e-9)
              ? "　⚠⚠ **与设定不符 —— 接线没接上，本档数据作废**" : ""));
        var dc = DesignCurrent.ForLine(d, p, null);
        Say($"设计电流（升温）段峰值 {string.Join("/", dc.SegPeakA.Select(v => v.ToString("0")))} A；舌片厚 {string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.00")))} mm");

        var sw = System.Diagnostics.Stopwatch.StartNew(); double last = 0;
        var tick = new Progress<string>(m =>
        {
            if (sw.Elapsed.TotalSeconds - last < 120) return;
            last = sw.Elapsed.TotalSeconds;
            Say($"     · [已跑 {sw.Elapsed.TotalSeconds:0} s] {m}");
        });
        var r = LineRunner.Run(lc, tick, default);
        Assert.True(r.Ok, "整线解不出来：" + r.Message);
        double V(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
        string W(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Where ?? "";
        Say($"用时 {sw.Elapsed.TotalMinutes:0.0} 分　耦合收敛 {r.Converged}　剩余估计 {r.CoupleRemainK:0.00} K　合计铂重 {r.TotalMassG:0} g　全过 {r.AllOk}");
        // R48 G1（2026-09-15，Opus 5）：逐片实际配方（证据头写的是生产配方常量；这里印跑出来的，对不上的片单列）
        Say("   配方（逐片实际）：" + r.RecipeSummary());
        foreach (var dev in r.RecipeDeviations) Say("   ⚠ 配方与生产不同：" + dev);
        Say($"判据：净流入 {V(LineResult.Key.NetFlux):+0.000;-0.000} W（{W(LineResult.Key.NetFlux)}）　圆盘区 {V(LineResult.Key.DiscTemp):+0.000;-0.000} K（{W(LineResult.Key.DiscTemp)}）"
          + $"　增量温降 {V(LineResult.Key.FlangeDip):+0.000;-0.000} K（{W(LineResult.Key.FlangeDip)}）　截面 J {V(LineResult.Key.SectionJ):0.00}　管强度利用率 {V(LineResult.Key.TubeStrength):0.000}");
        for (int i = 0; i < r.Segments.Length; i++)
        {
            var s = r.Segments[i];
            double tMid = s.X.Length > 0 && s.TMetal.Length == s.X.Length
                ? s.TMetal[Array.BinarySearch(s.X, 0.5 * (s.X[0] + s.X[^1])) is int k && k >= 0 ? k : Math.Min(~k, s.X.Length - 1)]
                : double.NaN;
            Say($"   段{i}　设定 {s.SetpointC:0.0} °C　中点 {tMid:0.00} °C　电流 {s.CurrentA:0} A　管 J {s.TubeJAPerMm2:0.00}　功率 {s.PowerW:0} W"
              + $"　管根 A/B {s.TRootAC:0.00}/{s.TRootBC:0.00}　增量温降 A/B {s.FlangeDipAK:+0.000;-0.000}/{s.FlangeDipBK:+0.000;-0.000} K");
        }
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            var f = r.Flanges[j];
            double setL = j == 0 ? r.Segments[0].SetpointC : j >= r.Segments.Length ? r.Segments[^1].SetpointC
                        : Math.Max(r.Segments[j - 1].SetpointC, r.Segments[j].SetpointC);
            Say($"   片{j}　电流 {f.CurrentA:0} A　抽热 {f.QFromTubeW:+0.000;-0.000} W　管根 {f.TRootC:0.00}　盘峰 {f.TDiscMaxC:0.00}（r={f.DiscMaxRMm:0.0} x={f.DiscMaxXMm:+0.0;-0.0}）"
              + $"　舌区峰 {f.TTabMaxC:0.00}（r={f.TabMaxRMm:0.0}）　盘峰−管根 {f.TDiscMaxC - f.TRootC:+0.00;-0.00}　盘峰−设定 {f.TDiscMaxC - setL:+0.00;-0.00}　舌区峰−设定 {f.TTabMaxC - setL:+0.00;-0.00} K");
        }
        if (r.Flanges.Length >= 4 && r.Segments.Length >= 3)
        {
            double kw0 = r.Segments[0].FlangeDipAK / r.Flanges[0].QFromTubeW, kw3 = r.Segments[^1].FlangeDipBK / r.Flanges[^1].QFromTubeW;
            Say($"   端片 K/W（增量温降 ÷ 抽热，截距按 0 估）：片0 {kw0:0.00}　片3 {kw3:0.00}");
        }
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
