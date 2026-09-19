using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 第二批保温扫描（2026-09-14，Opus 5 写；规格、读法出自物理把关人第八轮）：
/// **在入口片的推算可行带（管保温 7.5～8.5、入口片圆盘保温 9～10.5 mm）上铺小网格，端片按热偶读数基准判。**
///
/// ══ 这批与第一批不同的地方（跑之前定下）
///   · **共用片抽热只算一次**（DesignInputs.SplitSharedFlangeDraw = true，内部片的抽热两侧各半）。数值把关人：默认的「两段各扣一次」让管↔法兰热收支差 −27 W、
///     共用片抽热差 4.8 W／管根差 13 K（R48_探针二_共用片双扣对照），比耦合噪声大一个数量级；物理把关人：能量守恒要求各半，收敛后与「共享节点」等价。B5 是只差这个开关的正对照。
///   · 耦合容差 0.25 K（R48_耦合续跑_2026-09-14.txt：0.25 K 收敛点离不动点只差 0.011 K / 0.004 W，剩余估计不低估）。
///   · 逐片圆盘保温（DesignSpec.DiscInsulMm → FlangePlate.DiscInsulThickMm → LineRunner 逐片 p2），本批新接。
///   · 0.5 mm、生产配方（保温按半径 + 分界格混合 + 压接锚点）、管壁 0.8、舌片厚按各档设计电流重算。
///
/// ══ 五档（厚度都是 0.5 mm 的整数倍）
///   片3：圆盘 10 / 舌 10.5；片1：圆盘 3.5 / 舌 3.5；片2：圆盘 5 / 舌 5（共用片本批不判）；入口片舌 5。
///   B1 管 7.5 入口圆盘 9.0　B2 管 7.5 入口圆盘 10.5　B3 管 8.5 入口圆盘 9.0　B4 管 8.5 入口圆盘 10.5　B5 = B2 但共用片双扣（开关关）
///
/// ══ 读法（物理把关人，跑前写下）
///   1. 只判端片（片0、片3）。控温热偶在段中点（用户 2026-09-14），「看不见的过热」按热偶读数（段设定温度）判：
///      该片最热的铂（盘峰与舌区峰取大）− 该段设定 ≤ 5 K；0 &lt; 抽热；该接头增量温降 ≤ 10 K。其余硬判据全列。
///      ⚠ 这是物理把关人按用户两句原话推的口径，**还没写进判据代码**，也还要通知用户（可否决）；现行判据（盘峰 − 管根）并列打印。
///   2. 入口片圆盘保温 &gt; 8 mm，一维保温模型可信度要由截面二维旁算把关 —— 宣告可行之前的门，本批只记录。
///   3. 四档（B1～B4）任一端片按上面口径过线 ⇒ 在那一点做复核网格阶梯；都不过 ⇒ 报「这个形状上管保温 + 法兰保温在一维模型可信范围内不可行」，转形状搜索。
///   4. B5 vs B2：管↔法兰热收支差应趋近 0（开关开）；共用接头「增量温降 ÷ 共用片抽热」应降到约 1.2 K/W（物理把关人预测）。
/// ⚠ 管保温仍是全线一个值；用户说保温是绕的、可以不等厚，沿管长的厚度分布本批不动。
/// </summary>
[Trait("速度", "慢")]
public class R48SecondBatchTests
{
    private readonly ITestOutputHelper _out;
    public R48SecondBatchTests(ITestOutputHelper o) { _out = o; }

    [Fact] public void B1_管7点5_入口盘9() => Run("B1", 7.5, 9.0, true);
    [Fact] public void B2_管7点5_入口盘10点5() => Run("B2", 7.5, 10.5, true);
    [Fact] public void B3_管8点5_入口盘9() => Run("B3", 8.5, 9.0, true);
    [Fact] public void B4_管8点5_入口盘10点5() => Run("B4", 8.5, 10.5, true);
    [Fact] public void B5_同B2_双扣() => Run("B5", 7.5, 10.5, false);

    private void Run(string tag, double tube, double disc0, bool split)
    {
        var sb = new StringBuilder();
        string file = Path.Combine(Root(), "deliverable", $"R48_第二批保温扫描_{tag}_2026-09-14.txt");
        // ★ R48 G1（2026-09-15，Opus 5；数值把关人第十三轮）：写文件改走 EvidenceFile —— 文件头写明代码（git HEAD、Core 改动指纹）、配方、耦合容差、共用片抽热与工况；
        //   同名文件已存在且头不同（或是没有头的旧证据）⇒ 不覆盖，改写到带时间戳的新文件。算法一行不动。
        //   证据头要算例，算例建好后才打开（下面 ev 赋值处）；在那之前 Say 只进缓冲，打开时整份写出。
        EvidenceFile? ev = null;
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { ev?.Write(sb.ToString()); } catch { }
        }
        var p = new DesignInputs { SplitSharedFlangeDraw = split };
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.TubeInsulMm = tube;
        d.DiscInsulMm = new[] { disc0, 3.5, 5.0, 10.0 };
        d.TabInsulMm = new[] { 5.0, 3.5, 5.0, 10.5 };
        d = d.Fit();
        d.SizeTongues(p);
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        var lc = d.BuildCase(p, checkRamp: false);
        MeshAdapt.RefineWholeMesh(lc, 0.5, radius);
        lc.CoupleTolK = 0.25;
        lc.CoupleMaxRounds = Math.Max(lc.CoupleMaxRounds, 4000);
        ev = EvidenceFile.Open(file, EvidenceHeader.ForLineCase($"R48 第二批保温扫描 {tag}", lc));
        _out.WriteLine($"证据文件：{ev.Path}（{ev.Reason}）");
        try { ev.Write(sb.ToString()); } catch { }
        if (ev.Redirected) Say("⚠ 证据文件改道：" + ev.Reason);
        Say($"R48 第二批保温扫描 {tag}（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　0.5 mm 细区半径 {radius:0}　耦合容差 {lc.CoupleTolK} K　共用片抽热{(split ? "各半（只算一次）" : "双扣（两段各扣一次）")}");
        var plates = lc.FlangePlates;
        Say($"设定 管保温 {tube:0.0}　逐片圆盘 {string.Join("/", d.DiscInsulMm.Select(v => v.ToString("0.0")))}　逐片舌 {string.Join("/", d.TabInsulMm.Select(v => v.ToString("0.0")))} mm；"
          + $"算例实际 管保温层 {lc.Base.Layer1.ThicknessMm:0.0}　板件圆盘保温 {string.Join("/", plates.Select(g => g.DiscInsulThickMm.ToString("0.0")))}　板件舌保温 {string.Join("/", plates.Select(g => g.TabInsulThickMm.ToString("0.0")))}　双扣开关 {(lc.Base.SplitSharedFlangeDraw ? "各半" : "双扣")}"
          + ((Math.Abs(lc.Base.Layer1.ThicknessMm - tube) > 1e-9 || plates.Select((g, j) => Math.Abs(g.DiscInsulThickMm - d.DiscInsulMm[j])).Max() > 1e-9 || lc.Base.SplitSharedFlangeDraw != split)
              ? "　⚠⚠ **与设定不符 —— 接线没接上，本档作废**" : ""));
        Say($"舌片厚 {string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.00")))} mm");

        var sw = System.Diagnostics.Stopwatch.StartNew(); double last = 0;
        var tick = new Progress<string>(m =>
        {
            if (sw.Elapsed.TotalSeconds - last < 180) return;
            last = sw.Elapsed.TotalSeconds;
            Say($"     · [已跑 {sw.Elapsed.TotalSeconds:0} s] {m}");
        });
        var r = LineRunner.Run(lc, tick, default);
        Assert.True(r.Ok, "整线解不出来：" + r.Message);
        Say($"用时 {sw.Elapsed.TotalMinutes:0.0} 分　耦合收敛 {r.Converged}　剩余估计 {r.CoupleRemainK:0.000} K　合计铂重 {r.TotalMassG:0} g　全过 {r.AllOk}");
        // R48 G1（2026-09-15，Opus 5）：逐片实际配方（证据头写的是生产配方常量；这里印跑出来的，对不上的片单列）
        Say("   配方（逐片实际）：" + r.RecipeSummary());
        foreach (var dev in r.RecipeDeviations) Say("   ⚠ 配方与生产不同：" + dev);
        foreach (var n in r.Notes.Where(x => x.Contains("外层耦合"))) Say("   " + n);
        Say("   判据全表：");
        foreach (var c in r.Checks)
            Say($"     {(c.Undetermined ? "判不了" : c.Ok ? "过　" : "不过")}　{Criteria.Plain(c.Name)}　{c.Actual:0.###} {c.Unit}（限 {(c.LessIsBetter ? "≤" : "≥")} {c.Limit:0.###}，{c.Where}）");
        for (int i = 0; i < r.Segments.Length; i++)
        {
            var s = r.Segments[i];
            Say($"   段{i}　设定 {s.SetpointC:0.0}　电流 {s.CurrentA:0} A　管 J {s.TubeJAPerMm2:0.00}　管根 A/B {s.TRootAC:0.00}/{s.TRootBC:0.00}　基线 A/B {s.BaseTRootAC:0.00}/{s.BaseTRootBC:0.00}"
              + $"　增量温降 A/B {s.FlangeDipAK:+0.000;-0.000}/{s.FlangeDipBK:+0.000;-0.000} K");
        }
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            var f = r.Flanges[j];
            double setL = j == 0 ? r.Segments[0].SetpointC : j >= r.Segments.Length ? r.Segments[^1].SetpointC
                        : Math.Max(r.Segments[j - 1].SetpointC, r.Segments[j].SetpointC);
            double pMax = Math.Max(f.TDiscMaxC, f.TTabMaxC);
            Say($"   片{j}　电流 {f.CurrentA:0} A　抽热 {f.QFromTubeW:+0.000;-0.000} W　管根 {f.TRootC:0.00}　盘峰 {f.TDiscMaxC:0.00}（r={f.DiscMaxRMm:0.0}）　舌区峰 {f.TTabMaxC:0.00}（r={f.TabMaxRMm:0.0}）"
              + $"　盘峰−管根 {f.TDiscMaxC - f.TRootC:+0.00;-0.00}　最热铂−设定 {pMax - setL:+0.00;-0.00} K　{f.InsulRule}");
        }
        // 端片按热偶读数口径（物理把关人第八轮）
        if (r.Flanges.Length >= 2 && r.Segments.Length >= 1)
        {
            foreach (int j in new[] { 0, r.Flanges.Length - 1 })
            {
                var f = r.Flanges[j];
                var s = j == 0 ? r.Segments[0] : r.Segments[^1];
                double dip = j == 0 ? s.FlangeDipAK : s.FlangeDipBK;
                double pm = Math.Max(f.TDiscMaxC, f.TTabMaxC) - s.SetpointC;
                bool ok = pm <= 5 && f.QFromTubeW > 0 && dip <= 10;
                Say($"★ 片{j}（端片，热偶读数口径）：最热铂−设定 {pm:+0.00;-0.00} K（≤ 5）　抽热 {f.QFromTubeW:+0.000;-0.000} W（> 0）　增量温降 {dip:+0.000;-0.000} K（≤ 10）⇒ {(ok ? "三条都过" : "有不过")}");
            }
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
