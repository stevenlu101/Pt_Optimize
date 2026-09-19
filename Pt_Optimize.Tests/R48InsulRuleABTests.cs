using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 自查探针（2026-09-14，Opus 5 写）：**保温按半径这一改，能量账的方向对不对？**
/// ⛔ 2026-09-14 14:19 更正（Opus 5；物理把关人第五轮查出，deliverable/R48_接头电流核对_2026-09-14.txt 核实）：
///   本探针的片电流 1214 / 2102 / 2102 / 1214 A 是**升温设计电流**，不是整线稳态接头电流（1213 / 1984 / 1817 / 1022 A，导航网格整线）；
///   焦耳热比约 1.00 / 1.12 / 1.34 / 1.41。管根也不是整线值（整线 1172.63 / 1151.19 / 1093.09 / 1056.80 °C）。
///   ⇒ 同一工作点上的配对比较结论可用，片1～片3 的绝对值（抽热、敏感度、误差量级）**不代表设计工作点**，不许拿去做规划或判据。
///
/// 起因（实测与物理预期方向相反）：同设计、同 0.5 mm 网格、半径 59，
///   保温按 x：管孔净流入 −1.760 W、增量温降 +0.586 K；
///   保温按半径：管孔净流入 **−15.076 W**、增量温降 **−10.055 K**，四片抽热全降。
/// 而改动只把 −x 半个圆盘从舌保温换成法兰保温 2.5 mm：片0 4.6→2.5、片3 7.5→2.5（**变薄**，应多散热、抽热应升），
/// 片1 2.1→2.5、片2 2.9→2.5。变薄却散热更少，讲不通 ⇒ 要么改动有 bug，要么我对两张损失表的理解错了。
/// ⛔ 2026-09-14 下午更正（Opus 5）：**是我的前提错了，实现没有 bug**。圆盘保温在算例里是 DesignSpec.FlangeInsulMm = 20 mm，
///   不是 DesignInputs 默认的 2.5。p2 = Clone(lc.Base) 用的正是 20 mm ⇒ 四片 −x 半盘都是**变厚**，散热减、抽热降，方向对。
///   12:51 那份输出里的「判读」行按 2.5 判，结论写反了（数据列没错）；源码已改为按算例实际值判。
///
/// 做法：**同一片、同一张网格、管侧固定**（没有耦合），只切换保温规则（insulDiscRadiusMm = NaN 即旧 x 规则），
/// 逐项打能量账与「换了保温的格子」。只记录；最后按物理预期给判读：
///   片0、片3 的 −x 半盘变薄 ⇒ 散热应增、抽热应升。实测相反 ⇒ 实现有问题，重解与其余结论全部先停。
/// </summary>
[Trait("速度", "慢")]
public class R48InsulRuleABTests
{
    private readonly ITestOutputHelper _out;
    public R48InsulRuleABTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 同一片同一网格只切保温规则_能量账逐项对照()
    {
        var sb = new StringBuilder();
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
            try { File.WriteAllText(DeliverableOut.Stamped("R48_保温规则AB_2026-09-14.txt"), sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 自查：保温按 x 与按半径逐项对照（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　单片、管侧固定、0.5 mm、半径 59");

        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        var p = new DesignInputs();
        d.SizeTongues(p);
        var lc = d.BuildCase(p, checkRamp: false);
        double[] iA = { 1214, 2102, 2102, 1214 };
        double[] tRoot = { 1141.8, 1107.8, 1057.8, 1038.0 };
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        const double h = 0.5, R = 59.0;
        double coarse = lc.MeshCoarseMm / lc.MeshFineMm * h;

        Say($"法兰保温（圆盘，算例实际值）{lc.Base.FlangeInsulThickMm:0.0} mm；舌保温逐片 {string.Join("/", d.TabInsulMm.Select(v => v.ToString("0.0")))} mm");
        Say("");
        for (int j = 0; j < 4; j++)
        {
            var g = d.Plate(j, d.DiscFloorMm(p));
            g.HoleRadiusMm = holeR;
            var (xa, za) = FlangeMesher.AnchorsOf(g);
            var tf = FlangeMesher.Rasterize(g, Math.Max(h / 4, 0.02));
            // 口径钉住（2026-09-14 Opus 5）：生产默认已改为压接整面接触 + 自相似压接细带，本探针显式钉回改动前口径（只钉外圈、不铺细带），deliverable 里同名证据文件不换口径
            var m = FlangeMesher.BuildFromField(tf, holeR, 0, h, coarse, R, lc.Base.BusbarClampLengthMm, h, 0, g.TwoTabs, xa, za, 1.5 * h,
                                                clampBandMm: 0, clampFullFace: false);
            double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
            var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet;
            if (j < lc.ClampTempC.Length) p2.BusbarClampTempC = lc.ClampTempC[j];
            var sc = ShellCurrent.SolveFor(lc, m, iA[j], Materials.PtResistivity(tSet) * 1e3, tSet);

            ShellThermalResult Th(double insulR) => ShellThermal.Solve(m, sc.JMagAPerMm2, p2, tRoot[j], g.InsulBoundaryXResolved, g.TwoTabs,
                tabBoundaryX: g.Tangent().X, tabInsulThickMm: d.TabInsulMm[j], discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: insulR);
            var ox = Th(double.NaN);
            var nr = Th(g.InsulDiscRadiusMm);

            // 换了保温的格子：旧规则判「法兰保温」与新规则判「法兰保温」不一致的那些
            double xb = g.InsulBoundaryXResolved, r2 = g.DiscRadiusMm * g.DiscRadiusMm;
            int toDisc = 0, toTab = 0; double aToDisc = 0, aToTab = 0;
            for (int i = 0; i < m.CellCount; i++)
            {
                double cx = m.Centroid[i].X, cz = m.Centroid[i].Z;
                bool oldIns = cx >= xb, newIns = cx * cx + cz * cz <= r2;
                if (!oldIns && newIns) { toDisc++; aToDisc += m.Area[i]; }
                if (oldIns && !newIns) { toTab++; aToTab += m.Area[i]; }
            }
            Say($"── 片{j}　切点 x = {xb:+0.000;-0.000}　舌保温 {d.TabInsulMm[j]:0.0} mm");
            Say($"   换成法兰保温的格子 {toDisc} 个 / {aToDisc:0.0} mm²　　换成舌保温的格子 {toTab} 个 / {aToTab:0.0} mm²");
            Say($"   {"",8}{"抽热W",10}{"发热W",10}{"散热W",10}{"盘发热",9}{"盘散热",9}{"舌发热",9}{"舌散热",9}{"夹持W",9}{"最高温C",10}");
            string Line(string tag, ShellThermalResult t) =>
                $"   {tag,-8}{t.QFromTubeW,10:+0.000;-0.000}{t.QGenW,10:0.00}{t.QLossW,10:0.00}{t.QGenDiscW,9:0.00}{t.QLossDiscW,9:0.00}"
              + $"{t.QGenTabW,9:0.00}{t.QLossTabW,9:0.00}{t.QToClampW,9:0.00}{t.TMaxC,10:0.00}";
            Say(Line("按 x", ox));
            Say(Line("按半径", nr));
            Say($"   差（半径−x）：抽热 {nr.QFromTubeW - ox.QFromTubeW:+0.000;-0.000}　散热 {nr.QLossW - ox.QLossW:+0.000;-0.000}　发热 {nr.QGenW - ox.QGenW:+0.000;-0.000}");
            Say($"   保温规则字串：按 x「{ox.InsulRule}」　按半径「{nr.InsulRule}」");
            double discIns = lc.Base.FlangeInsulThickMm;   // 2026-09-14 更正（Opus 5）：原取 DesignInputs 默认 2.5，不是算例实际值
            bool thinner = d.TabInsulMm[j] > discIns + 1e-9;   // −x 半盘从舌保温换成更薄的法兰保温
            if (toDisc > 0)
                Say(thinner
                    ? $"   判读：−x 半盘变薄（{d.TabInsulMm[j]:0.0}→{discIns:0.0}）⇒ 物理上散热应增、抽热应升。实测散热{(nr.QLossW > ox.QLossW ? "增" : "**减**")}、抽热{(nr.QFromTubeW > ox.QFromTubeW ? "升" : "**降**")}"
                      + (nr.QLossW > ox.QLossW && nr.QFromTubeW > ox.QFromTubeW ? " ⇒ 方向对。" : " ⇒ **方向反了，实现或理解有问题**。")
                    : $"   判读：−x 半盘变厚（{d.TabInsulMm[j]:0.0}→{discIns:0.0}）⇒ 物理上散热应减、抽热应降。实测散热{(nr.QLossW < ox.QLossW ? "减" : "**增**")}、抽热{(nr.QFromTubeW < ox.QFromTubeW ? "降" : "**升**")}"
                      + (nr.QLossW < ox.QLossW && nr.QFromTubeW < ox.QFromTubeW ? " ⇒ 方向对。" : " ⇒ **方向反了，实现或理解有问题**。"));
            Say("");
        }
        Assert.Contains("片3", sb.ToString());
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
