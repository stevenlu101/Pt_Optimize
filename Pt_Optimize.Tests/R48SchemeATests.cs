using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 方案 A 生死点（2026-09-14，Opus 5 写；规格与预测出自物理把关人第五、六轮）：
/// **圆盘与舌片取同一个保温厚度 X（舌根没有保温台阶）时，三条法兰判据有没有解？**
///
/// ══ 为什么问这个
/// 保温分界敏感度（deliverable/R48_保温分界敏感度_2026-09-14.txt、门六）：抽热对「保温在舌根哪里由厚变薄」的外侧斜率约 −3.4（片0）W/mm，
/// 实际包层收尾做不到毫米级 ⇒ 盘与舌根保温厚度差 Δ 越大，现场收尾公差越紧（物理把关人公差表：20 对 4.6 约 ±0.6 mm，差 0.8 mm 约 ±5 mm）。
/// 方案 A = 不在舌根放台阶：圆盘保温 = 舌保温 = X。它对收尾不敏感，代价是失去「舌薄治圆盘区、盘厚治增量温降」的拆分。
///
/// ══ 跑前登记的预测（物理把关人第六轮，推理：两根杠杆整线耦合数据按可叠加外推，锚点圆盘 20／舌 4.6 时 +13.85 K）
///   片0 圆盘区−管温：X = 1.5 约 6 K；3 约 11 K；5 约 18 K；7 约 27 K ⇒ 要 ≤ 5 K 需 X ≲ 1.2 mm；
///   而片0 抽热进 0～4 W 在舌保温 4.6 时就要圆盘保温 ≥ 6.6 mm，舌再薄要更厚 ⇒ **方案 A 对片0 大概率不可行，两边差 5 mm 以上**。
///   证伪条件：片0 在 X = 3 时圆盘区−管温 ≤ 5 K；或 X = 1.5、3 两点片0 抽热落进 0～4 W ⇒ 叠加假设错，方案 A 还活着。
///
/// ══ 读法（物理把关人）
///   · **逐片读**，不读四片取最值后的判据（上午「四片同值区间不相交」的误读就出在这）；每个接头的增量温降 ≈ 自己那片抽热 × 约 2.4 K/W。
///   · **只用片0 判方案 A 的死活**：片0 是端片，不受共用片双扣开关影响，历来圆盘区最高温最差。
///   · 0.5 mm 整线、保温按半径、细区半径按 RequiredMeshFor；电流与管根由整线耦合给出（不是单片探针那组升温电流）。
/// ⚠ 管保温仍取设计值 5 mm（DesignSpec.TubeInsulMm），不是现场原做法 2–3 mm —— 已列进问用户的清单；本探针期间不改，以免与今天其它结果不可比。
/// ⚠ 一维保温模型在圆环 4.2 mm 宽上的误差（二维旁算）未计。
/// 只记录与按上面的证伪条件判读，不改设计。
/// </summary>
[Trait("速度", "慢")]
public class R48SchemeATests
{
    private readonly ITestOutputHelper _out;
    public R48SchemeATests(ITestOutputHelper o) { _out = o; }

    [Fact] public void 方案A_X1点5() => Run(1.5);
    [Fact] public void 方案A_X3() => Run(3.0);
    [Fact] public void 方案A_X5() => Run(5.0);
    [Fact] public void 方案A_X7() => Run(7.0);

    private void Run(double x)
    {
        var sb = new StringBuilder();
        string file = Path.Combine(Root(), "deliverable", $"R48_方案A_X{x:0.0}mm_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { x, x, x, x };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.FlangeInsulMm = x;
        d = d.Fit();
        d.SizeTongues(p);
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        var lc = d.BuildCase(p, checkRamp: false);
        MeshAdapt.RefineWholeMesh(lc, 0.5, radius);

        Say($"R48 方案 A（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　管壁 0.8 落点几何，圆盘保温 = 舌保温 = X，保温按半径，0.5 mm，细区半径 {radius:0}");
        Say($"设定 X = {x:0.0}；算例实际圆盘保温 {lc.Base.FlangeInsulThickMm:0.0} mm，舌保温 {string.Join("/", d.TabInsulMm.Select(v => v.ToString("0.0")))} mm"
          + (Math.Abs(lc.Base.FlangeInsulThickMm - x) > 1e-9 ? "　⚠⚠ **圆盘保温与设定不符 —— 接线没接上，本行数据作废**" : ""));
        Say($"管保温 {d.TubeInsulMm:0.0} mm（设计取值，非现场原做法 2–3 mm）；舌片厚 {string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.00")))} mm");

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
        Say($"段稳态电流 {string.Join(" / ", r.Segments.Select(s => s.CurrentA.ToString("0")))} A；接头电流 {string.Join(" / ", r.Flanges.Select(f => f.CurrentA.ToString("0")))} A");
        Say($"判据（四片取最值，只作参考）：管孔净流入 {V(LineResult.Key.NetFlux):+0.000;-0.000} W（{W(LineResult.Key.NetFlux)}）"
          + $"　圆盘区最高温 {V(LineResult.Key.DiscTemp):+0.000;-0.000} K（{W(LineResult.Key.DiscTemp)}）"
          + $"　法兰增量温降 {V(LineResult.Key.FlangeDip):+0.000;-0.000} K（{W(LineResult.Key.FlangeDip)}）　全过 {r.AllOk}");
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            var f = r.Flanges[j];
            Say($"   片{j}　抽热 {f.QFromTubeW,8:+0.000;-0.000} W　管根 {f.TRootC,8:0.00} °C　圆盘区−管温 {f.TDiscMaxC - f.TRootC,7:+0.000;-0.000} K"
              + $"（峰位 r={f.DiscMaxRMm:0.0} x={f.DiscMaxXMm:+0.0;-0.0}）　舌区峰 {f.TTabMaxC - f.TRootC:+0.00;-0.00} K（r={f.TabMaxRMm:0.0}）　{f.InsulRule}");
        }
        for (int i = 0; i < r.Segments.Length; i++)
            Say($"   段{i}　增量温降 A 端 {r.Segments[i].FlangeDipAK:+0.000;-0.000} K　B 端 {r.Segments[i].FlangeDipBK:+0.000;-0.000} K");

        var f0 = r.Flanges[0];
        double disc0 = f0.TDiscMaxC - f0.TRootC, q0 = f0.QFromTubeW;
        var pred = new System.Collections.Generic.Dictionary<double, double> { [1.5] = 6, [3.0] = 11, [5.0] = 18, [7.0] = 27 };
        Say($"★ 片0：圆盘区−管温 {disc0:+0.00;-0.00} K（预测约 {pred[x]:0} K）；抽热 {q0:+0.00;-0.00} W（0～4 W 为窗口）");
        if (x == 3.0)
            Say(disc0 <= 5.0 ? "   ⇒ X = 3 时片0 圆盘区 ≤ 5 K：**证伪**，叠加假设错，方案 A 还活着。" : "   ⇒ X = 3 时片0 圆盘区 > 5 K：与预测方向一致（这一条没证伪）。");
        if (x == 1.5 || x == 3.0)
            Say(q0 >= 0 && q0 <= 4 ? $"   ⇒ X = {x:0.0} 时片0 抽热落进 0～4 W：**证伪**，方案 A 还活着。" : $"   ⇒ X = {x:0.0} 时片0 抽热不在 0～4 W（这一条没证伪）。");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
