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
/// R48 仪器（2026-09-13，Opus 5 写）：**中带收敛序列**。
///
/// 为什么要它：R47 把网格轴修对之后，加密复算（<see cref="MeshVerify"/>）逐档减的是**内带**（管孔那一圈），
/// 中带（盘面与舌根）固定在 <c>RequiredMeshFor</c> 给的特征尺寸 h0 上，只在最后做一次「中带确认」（h0 → h0/2）。
/// 两个内置档重解后这一步**都没过**：管孔净流入差 +6.1／+5.9 W、法兰增量温降差 +10.5／+11.8 K
/// ⇒ 判据受中带粗糙度支配，「内带加密到数不再变」的结论不成立，那一档的判据值不可信。
///
/// ⚠ 方向很要紧：中带确认报的是**变化量**。把它加回去，中带 h0/2 上两个档的管孔净流入是 **正的**（+1.6／+1.2 W）、
/// 法兰增量温降 7.7／7.6 K（限 10）——也就是说「不过」是中带 1.0 mm 的离散误差，不是设计不行。
/// 但一档不算收敛：本仪器把中带 1.0 → 0.5 → 0.25 各解一次（内带钉在 0.25，它已被证明再减只动 0.2 W 以内），
/// 看这三个点收敛到哪里。**没有这条序列就写不出记录值**（不知道真值，写什么都是猜）。
///
/// 慢：中带 0.25 的整线一次解可能上小时。只记录、不判定。
/// </summary>
[Trait("速度", "慢")]
public class R48MidBandConvergenceTests
{
    private readonly ITestOutputHelper _out;
    public R48MidBandConvergenceTests(ITestOutputHelper o) { _out = o; }

    /// <summary>
    /// 管壁 0.8 三段重解的落点（deliverable/R48_重解_管壁08_2026-09-13.txt 第 3 轮）。
    /// ⚠ 舌片厚**不是旋钮**（= I/(J·带外最窄有效宽)，闭式），手填就会填错：
    ///   第一版仪器只填了板厚与舌保温，舌片厚留着旧记录的值 ⇒ 三档全部「出口峰值 4400 °C 越过铂熔点」。
    ///   正确做法是调 <see cref="DesignSpec.SizeTongues"/> 按当前几何重算，与求解器同一条闭式。
    /// </summary>
    private static DesignSpec W08Resolved(DesignInputs p)
    {
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };     // 约束盒下角：max(焊接屈曲/烧穿 0.60, 按 J=10 的截面)
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.40 };     // 求解器逐轮二分出来的落点
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        for (int j = 0; j < d.SlotSpanDeg.Length; j++) d.SlotSpanDeg[j] = 0;
        for (int j = 0; j < d.TabHoleRMm.Length; j++) d.TabHoleRMm[j] = 0;
        d = d.Fit();
        d.SizeTongues(p);                                     // 舌片厚按闭式重算（与求解器同一条路）
        d.Name = "管壁 0.8 · R48 重解落点（4245 g）";
        return d;
    }

    [Fact]
    public void 量中带收敛序列_管壁08重解落点()
    {
        var p = new DesignInputs();
        var d = W08Resolved(p);
        var sb = new StringBuilder();
        void Say(string s) { _out.WriteLine(s); sb.AppendLine(s); }

        Say($"设计：{d.Name}");
        Say($"  板厚 {string.Join("/", d.TabThickMm.Select(v => v.ToString("0.00")))}"
          + $"　舌保温 {string.Join("/", d.TabInsulMm.Select(v => v.ToString("0.00")))}"
          + $"　环倍率 {string.Join("/", d.RingMul.Select(v => v.ToString("0.00")))}"
          + $"　舌片厚（闭式重算）{string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.00")))}");
        Say("");
        Say("中带mm  内带mm      单元数    管孔净流入W  圆盘区最高温K  法兰增量温降K      合计g     用时");
        double? prevQ = null, prevD = null;
        foreach (double hMid in new[] { 1.0, 0.5, 0.25 })
        {
            var lc = d.BuildCase(p, checkRamp: false);
            lc.MeshFineMm = hMid;                 // 中带
            lc.MeshInnerMm = 0.25;                // 内带钉住（0.25 → 0.125 实测只动 0.17 W／0.29 K）
            lc.MeshInnerRadiusMm = MeshAdapt.InnerRadiusFor(d.HoleRadiusMm, Math.Max(d.TabThickMm.Max(), d.WallMm));
            var sw = Stopwatch.StartNew();
            var r = LineRunner.Run(lc, null, default);
            sw.Stop();
            if (!r.Ok) { Say($"{hMid,6:0.00}  {0.25,6:0.00}  解不出来：{r.Message}"); continue; }
            double Q(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
            double q = Q(LineResult.Key.NetFlux), dt = Q(LineResult.Key.DiscTemp), dip = Q(LineResult.Key.FlangeDip);
            Say($"{hMid,6:0.00}  {0.25,6:0.00}  {r.MeshCells,10}  {q,11:0.000}  {dt,13:0.000}  {dip,13:0.000}  {r.TotalMassG,9:0.0}  {sw.Elapsed.TotalMinutes,6:0.0} 分"
              + (prevQ is { } pq ? $"　（较上一档 管孔净流入 {q - pq:+0.000;-0.000}／法兰增量温降 {dip - prevD!.Value:+0.000;-0.000}）" : ""));
            // 逐片：整线判据取的是最差片，要定位到底哪一片在振荡（保温厚的片对网格最敏感）
            Say("        逐片：" + string.Join("　", r.Flanges.Select((f, j) =>
                $"片{j}{(j < d.TabInsulMm.Length ? $"(保温{d.TabInsulMm[j]:0.0})" : "")} 抽热 {f.QFromTubeW:+0.00;-0.00} 发热 {f.QGenW:0} 散热 {f.QLossW:0}")));
            prevQ = q; prevD = dip;
        }
        Say("");
        Say("读法：管孔净流入须为正、法兰增量温降 ≤ 10 K、圆盘区最高温 ≤ 5 K。");
        Say("这一列若在中带 0.5 → 0.25 之间不再动（容差：管孔净流入 0.5 W／法兰增量温降 1 K），就说明中带 0.5 够用，那一档的数可以当真值写进记录。");

        string dir = SolutionDir();
        File.WriteAllText(Path.Combine(dir, "deliverable", "R48_中带收敛_管壁08_2026-09-13.txt"), sb.ToString(), new UTF8Encoding(false));
    }


    /// <summary>盘Ø56 记录（两段）—— 论文 10.5 节案例、拓扑线基线；构造出来的，不是内置档。</summary>
    internal static DesignSpec Disc56()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 };
        d.TubeInsulMm = 10;
        d.DiscRadiusMm = 28; d.TabHalfWidthMm = 28; d.TabLengthMm = 140;
        double[] tb = { 0.60, 1.02, 0.60 }, tg = { 1.75, 3.03, 1.75 }, ins = { 5.1, 2.8, 8.6 };
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            int k = Math.Min(j, tb.Length - 1);
            d.TabThickMm[j] = tb[k]; if (j < d.TongueThickMm.Length) d.TongueThickMm[j] = tg[k];
            if (j < d.TabInsulMm.Length) d.TabInsulMm[j] = ins[k];
            if (j < d.RingMul.Length) d.RingMul[j] = 1.0;
            if (j < d.SlotSpanDeg.Length) d.SlotSpanDeg[j] = 0;
            if (j < d.TabHoleRMm.Length) d.TabHoleRMm[j] = 0;
        }
        d = d.Fit();
        d.Name = "盘Ø56 记录（两段）";
        return d;
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
