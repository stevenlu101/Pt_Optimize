using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 压接段整面接触 A/B（2026-09-14，Opus 5 写；规格与判读出自物理把关人第十轮）：
/// **铜排压接段只钉外圈（老口径）与整面接触，差多少？**
///
/// ══ 起因
/// 工作流读码 + 实验 b 数据：压接判定只打在**边界面**上，相邻那一圈格被当作电极与定温格；均匀 h=2 片0 钉 68 格，而 40 mm × 60 mm 整面应为 600 格。
/// 物理把关人：铜排单位长度导电能力约为舌片 35 倍、夹持在 450 °C ⇒ 整面等电位、整面趋近夹持温度是零阶正确模型；
/// 老口径里主要进电的内边（x = 舌尖 + 压接长）是内部面，没被钉；只钉外圈约等于把自由舌片拉长约 20 %（推理）。
///
/// ══ 设置（工作点取第二批 B2，出处 R48_第二批保温扫描_B2_2026-09-14.txt）
/// 设计同 B2：管保温 7.5、逐片圆盘 10.5/3.5/5.0/10.0、逐片舌 5.0/3.5/5.0/10.5，舌片厚按设计电流重算；
/// 片0 电流 1072 A、管根 1139.95 °C；片1 电流 1737 A、管根 1120.76 °C。单片、管侧固定，均匀网格 h = 1 / 0.5 / 0.25。
/// A = 老口径（ShellMesh.ClampCell 空），B = 整面（BuildFromField clampFullFace = true）。
///
/// ══ 跑前写死的判读（物理把关人）
///   任一片任一档 |Δ抽热| ≥ 0.5 W，或舌区峰变化 ≥ 2 K ⇒ **改成整面接触，排在任何进一步扫描之前**；否则记录，暂不改。
///   另报钉住格数（整面应 ≈ 压接面积 ÷ h²）与夹持带走的热，核「只钉外圈时压接区热锚点太弱」的方向。
/// </summary>
[Trait("速度", "慢")]
public class R48ClampFullFaceTests
{
    private readonly ITestOutputHelper _out;
    public R48ClampFullFaceTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 压接段只钉外圈与整面接触对照()
    {
        var sb = new StringBuilder();
        string file = Path.Combine(Root(), "deliverable", "R48_压接整面接触AB_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 压接段 只钉外圈 vs 整面接触（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　工作点同第二批 B2");
        var p = new DesignInputs { SplitSharedFlangeDraw = true };
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.TubeInsulMm = 7.5;
        d.DiscInsulMm = new[] { 10.5, 3.5, 5.0, 10.0 };
        d.TabInsulMm = new[] { 5.0, 3.5, 5.0, 10.5 };
        d = d.Fit();
        d.SizeTongues(p);
        var lc = d.BuildCase(p, checkRamp: false);
        double clampLen = lc.Base.BusbarClampLengthMm;
        Say($"舌片厚 {string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.00")))} mm　压接长 {clampLen:0.0} mm　夹持温度 {string.Join("/", lc.ClampTempC.Select(v => v.ToString("0")))} °C　铜排热导 {lc.Base.BusbarConductanceWPerK}");
        var cases = new[] { (j: 0, iA: 1072.0, tRoot: 1139.95), (j: 1, iA: 1737.0, tRoot: 1120.76) };
        bool change = false;
        foreach (var (j, iA, tRoot) in cases)
        {
            Say($"── 片{j}　电流 {iA:0} A　管根 {tRoot:0.00} °C");
            Say("    h      口径   单元    钉住格   抽热W      舌区发热W   夹持带走W   舌区峰°C    盘峰°C     最热铂−管根K");
            foreach (double h in new[] { 1.0, 0.5, 0.25 })
            {
                (double q, double tab, double disc, double gen, double clamp, int pinned, int cells) Solve(bool full)
                {
                    var g = lc.FlangePlates[j];
                    var (xa, za) = FlangeMesher.AnchorsOf(g);
                    var tf = FlangeMesher.Rasterize(g, Math.Max(h / 4, 0.02));
                    // 口径钉住（2026-09-14 Opus 5）：生产默认改为铺自相似压接细带后，本探针显式不铺（跑出 AB 文件时细带缺省是 0），同名证据文件不换口径
                    // 口径钉住（2026-09-15 Opus 5）：生产默认改为压接面上定温（配方 ⑤）后，本探针显式钉回形心整格（跑出 AB 文件时还没有面上口径）
                    var m = FlangeMesher.BuildFromField(tf, g.HoleRadiusMm, 0, h, h, 1e6, clampLen, h, 0, g.TwoTabs, xa, za, clampBandMm: 0, clampFullFace: full, clampFaceDirichlet: false);
                    double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
                    var sc = ShellCurrent.SolveFor(lc, m, iA, Materials.PtResistivity(tSet) * 1e3, tSet);
                    var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet;
                    if (j < lc.ClampTempC.Length) p2.BusbarClampTempC = lc.ClampTempC[j];
                    p2.FlangeInsulThickMm = g.DiscInsulEffectiveMm(lc.Base);
                    var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, tRoot, g.InsulBoundaryXResolved, g.TwoTabs,
                                                tabBoundaryX: g.Tangent().X, tabInsulThickMm: g.TabInsulThickMm,
                                                discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: g.InsulDiscRadiusMm);
                    Assert.True(th.Converged, $"片{j} h={h} {(full ? "整面" : "外圈")} 热场没收敛");
                    int pinned = full ? m.ClampCell.Count(b => b)
                                      : m.Faces.Where(f => f.B < 0 && f.Tag == ShellMesh.TagTabEnd).Select(f => f.A).Distinct().Count();
                    return (th.QFromTubeW, th.TTabMaxC, th.TDiscMaxC, th.QGenW, th.QToClampW, pinned, m.CellCount);
                }
                var a = Solve(false); var b = Solve(true);
                Say($"   {h,4:0.00}   外圈  {a.cells,7}  {a.pinned,6}  {a.q,9:+0.000;-0.000}  {a.gen,10:0.00}  {a.clamp,10:0.00}  {a.tab,10:0.00}  {a.disc,10:0.00}  {Math.Max(a.tab, a.disc) - tRoot,+10:+0.00;-0.00}");
                Say($"   {h,4:0.00}   整面  {b.cells,7}  {b.pinned,6}  {b.q,9:+0.000;-0.000}  {b.gen,10:0.00}  {b.clamp,10:0.00}  {b.tab,10:0.00}  {b.disc,10:0.00}  {Math.Max(b.tab, b.disc) - tRoot,+10:+0.00;-0.00}");
                double dq = b.q - a.q, dtab = b.tab - a.tab;
                bool big = Math.Abs(dq) >= 0.5 || Math.Abs(dtab) >= 2.0;
                if (big) change = true;
                Say($"          差（整面−外圈）：抽热 {dq:+0.000;-0.000} W　舌区峰 {dtab:+0.00;-0.00} K　盘峰 {b.disc - a.disc:+0.00;-0.00} K　发热 {b.gen - a.gen:+0.00;-0.00} W　夹持 {b.clamp - a.clamp:+0.00;-0.00} W　{(big ? "⇒ 超门槛" : "")}");
            }
        }
        Say("");
        Say(change ? "★ 有片有档 |Δ抽热| ≥ 0.5 W 或舌区峰变化 ≥ 2 K ⇒ 改成整面接触，排在任何进一步扫描之前。"
                   : "★ 各档都在门槛内 ⇒ 记录，暂不改。");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
