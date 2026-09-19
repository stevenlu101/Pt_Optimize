using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 追查（2026-09-13，Opus 5 写）：**「圆盘区最高温」这条硬安全线到底盖住了多少片、多少面积？**
///
/// ══ 起因（用户 09-13：「下了定论后再反复思考是否正确、是否有遗漏」）
///
/// 复核报 管孔净流入 −1.760 W（热往管里灌），同一次却报 圆盘区最高温 − 管温 = **−0.041 K**
/// （圆盘区整个比管子**冷**）。冷的一侧不该往管里送热。两条判据自称是「同一条安全线的两个视角」，
/// 符号却对不上 ⇒ 至少有一条没在量它自称在量的东西。
///
/// ══ 嫌疑（读码得到，本仪器去证实或证伪）
///
/// 分区规则（`ShellThermal`）：
/// <code>
///   onTab = symmetricInsul ? |x| > |xb| : x &lt; xb        其中 xb = 板的切点 x
/// </code>
/// 而这两个内置设计**舌半宽 = 盘半径**（都是 30），于是切点落在 **x = 0**：
///   · 单舌片（端片）：x &lt; 0 全算舌片 ⇒ 圆盘区只剩 **+x 半边**；
///   · 双舌片（共用片）：|x| &gt; 0 几乎每格都算舌片 ⇒ 圆盘区**可能是空的** ⇒ TDiscMaxC = NaN。
///
/// 而判据那一行是 `flanges.Where(f => !double.IsNaN(f.TDiscMaxC))...FirstOrDefault()` ——
/// **NaN 的片被静默滤掉**；「无法判定」那条分支只在**所有片都 NaN** 时才触发。
/// 若共用片（电流最大的那两片，2102 A 对端片 1214 A）正好全是 NaN，
/// 这条硬安全线就从来没在它们身上判过，而输出看不出来。
///
/// ⚠ 这与代码自己写下的纪律「**判据消失比判据不过危险得多**」是同一件事 ——
///   当年补的 else 只堵了「全部消失」，没堵「消失一半」。
///
/// ══ 本仪器
///
/// 逐片打印：是否双舌片、切点 x、圆盘区格数／面积、舌片区格数／面积、
/// TDiscMaxC 是不是 NaN、TTabMaxC、管孔净流入、以及**孔周那一圈被分到哪边**。
/// 只记录、不判定 —— 判定留给读完数之后。
/// </summary>
[Trait("速度", "慢")]
public class R48DiscZoneCoverageTests
{
    private readonly ITestOutputHelper _out;
    public R48DiscZoneCoverageTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 量圆盘区判据逐片盖住了什么()
    {
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        string log = DeliverableOut.Stamped("R48_圆盘区判据覆盖_2026-09-13.txt");
        var sb = new StringBuilder();
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(log, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }

        Say($"R48 追查：圆盘区判据的覆盖面（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        Say("管壁 0.8，设计 = 09-13 重解落到的那一点。导航网格（2.0 mm）即可 —— 问的是分区，不是分辨率。");
        Say("");

        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        d.SizeTongues(p);

        // 先把几何摆出来：切点在哪、是不是双舌片 —— 这两件决定分区
        double floor = d.DiscFloorMm(p);
        for (int j = 0; j < 4; j++)
        {
            var g = d.Plate(j, floor);
            Say($"片{j}：盘 R{g.DiscRadiusMm:0.0}　舌半宽 {g.TabEndHalfWidthMm:0.0}　"
              + $"切点 x {g.Tangent().X:+0.000;-0.000}　双舌片 {g.TwoTabs}　舌尖 x {g.TabTipXMm:0}");
        }
        Say("");
        Say("分区规则：onTab = 双舌片 ? |x| > |切点| : x < 切点　（ShellThermal）");
        Say("⇒ 切点 = 0 时：单舌片的圆盘区只剩 +x 半边；双舌片的圆盘区可能是空集。");
        Say("");

        var lc = d.BuildCase(p, checkRamp: false);
        MeshAdapt.RefineWholeMesh(lc, 2.0, lc.MeshFineRadiusMm);
        var r = LineRunner.Run(lc, null, default);
        Assert.True(r.Ok, "整线解不出来：" + r.Message);

        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        Say($"孔半径 {holeR:0.0} mm　—— 圆盘区峰位若落在 r≈孔半径 上，说明峰是**孔边那圈贴着定温面的格**，");
        Say("   而它们按边界条件本来就≈管温 ⇒ 这条判据被自己的边界条件钉死，等于没在判。");
        Say("");
        Say($"{"片",4}{"管孔净流入W",13}{"圆盘区最高温C",15}{"舌片区最高温C",15}{"管根C",9}{"圆盘区−管温K",14}{"峰位r",8}{"峰位x",9}{"峰J",7}");
        int nan = 0;
        foreach (var (f, j) in r.Flanges.Select((f, j) => (f, j)))
        {
            bool isNan = double.IsNaN(f.TDiscMaxC);
            if (isNan) nan++;
            Say($"{j,4}{f.QFromTubeW,13:+0.000;-0.000}"
              + $"{(isNan ? "NaN（空集）" : f.TDiscMaxC.ToString("0.00")),15}"
              + $"{f.TTabMaxC,15:0.00}{f.TRootC,9:0.0}"
              + $"{(isNan ? "判不了" : (f.TDiscMaxC - f.TRootC).ToString("+0.000;-0.000")),14}"
              + $"{(isNan ? "—" : f.DiscMaxRMm.ToString("0.0")),8}"
              + $"{(isNan ? "—" : f.DiscMaxXMm.ToString("+0.0;-0.0")),9}"
              + $"{(isNan ? "—" : f.DiscMaxJAPerMm2.ToString("0.00")),7}");
        }
        Say("");
        Say($"{"片",4}{"舌片区−管温K",14}{"峰位r",8}{"峰位x",9}{"峰位z",9}{"峰J",7}{"峰厚",7}"
          + "　（判据只看圆盘区，这一列被有意排除 —— 但峰若在孔边，那就是漏判）");
        foreach (var (f, j) in r.Flanges.Select((f, j) => (f, j)))
            Say($"{j,4}{f.TTabMaxC - f.TRootC,14:+0.000;-0.000}"
              + $"{f.TabMaxRMm,8:0.0}{f.TabMaxXMm,9:+0.0;-0.0}{f.TabMaxZMm,9:+0.0;-0.0}"
              + $"{f.TabMaxJAPerMm2,7:0.00}{f.TabMaxThickMm,7:0.00}");
        Say($"　孔半径 {holeR:0.0} mm　⇒ 峰位 r 接近它 = 峰在孔边（紧贴管子），排除它是漏判；"
          + "峰位 x 远在负方向 = 峰在远处舌片上，排除它是对的。");

        // ★ 用户 09-13 追问：「抽热是几千瓦里的几瓦之差」这句的依据是什么？
        //   我原先是把两份不同仪器的输出拼起来说的，而且拿了**管子**的段功率当分母 —— 不作数。
        //   能量账是**逐片**结算的（EnergyResidualW = 发热 − 散热 + 管孔净流入 − 夹持流出），
        //   所以正确的分母是**这一片自己的发热**。在同一次场解里一起打出来，才有依据。
        Say("");
        Say("── 逐片能量账（同一次场解，同一口径 —— 这才是「小数之差」这句话该有的依据）");
        Say($"{"片",4}{"发热W",10}{"表面散热W",12}{"夹持流出W",12}{"管孔净流入W",13}{"残差W",10}{"抽热/发热",11}");
        foreach (var (f, j) in r.Flanges.Select((f, j) => (f, j)))
            Say($"{j,4}{f.QGenW,10:0.0}{f.QLossW,12:0.0}{f.QClampW,12:0.0}"
              + $"{f.QFromTubeW,13:+0.000;-0.000}{f.EnergyResidualW,10:+0.000;-0.000}"
              + $"{(f.QGenW > 1e-9 ? (f.QFromTubeW / f.QGenW * 100).ToString("0.000") + " %" : "—"),11}");
        Say($"　整线合计：发热 {r.Flanges.Sum(f => f.QGenW):0.0} W　"
          + $"管孔净流入 {r.Flanges.Sum(f => f.QFromTubeW):+0.000;-0.000} W");
        Say("　注：运行头部那个「段功率 kW」是**管子**的功率，不是法兰的发热，不能拿来当这里的分母。");
        Say("");
        Say($"圆盘区为空（TDiscMaxC = NaN）的片数：**{nan} / {r.Flanges.Length}**");
        var judged = r.Checks.FirstOrDefault(c => c.Name.StartsWith(LineResult.Key.DiscTemp, StringComparison.Ordinal));
        Say($"判据实际取的是：{judged?.Where ?? "—"}　值 {judged?.Actual:0.000} K　"
          + $"{(judged?.Undetermined == true ? "（报了无法判定）" : "（当作判过了）")}");
        Say("");
        Say("读法：");
        Say("  · nan > 0 且判据没报「无法判定」⇒ 这条硬安全线**在那几片上从来没判过**，而输出看不出来。");
        Say("  · 端片的圆盘区只剩 +x 半边 ⇒ 舌片侧（电流进管子那一侧）的孔边温度不在这条判据里。");
        Say("  · 若 nan 的正好是电流最大的共用片 ⇒ 漏掉的恰恰是最危险的两片。");

        Assert.Contains("圆盘区为空", sb.ToString());
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
