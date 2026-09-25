using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★ R48（2026-09-14，Opus 5 写）：保温分界圆上的格子按有料面积份额混合两种保温的热流 —— 快门三道 + 慢门一道。
/// ⛔ 2026-09-14 14:19 更正（Opus 5；物理把关人第五轮查出，deliverable/R48_接头电流核对_2026-09-14.txt 核实）：
///   本探针的片电流 1214 / 2102 / 2102 / 1214 A 是**升温设计电流**，不是整线稳态接头电流（1213 / 1984 / 1817 / 1022 A，导航网格整线）；
///   焦耳热比约 1.00 / 1.12 / 1.34 / 1.41。管根也不是整线值（整线 1172.63 / 1151.19 / 1093.09 / 1056.80 °C）。
///   ⇒ 同一工作点上的配对比较结论可用，片1～片3 的绝对值（抽热、敏感度、误差量级）**不代表设计工作点**，不许拿去做规划或判据。
///
/// 快门（构造性质，不看数值大小）：
///   ① LossTable.Blend 端点：f = 0 / 1 逐位等于两张原表；两张表逐位相同时混合结果逐位不变。
///   ② 份额量法：+x 半边（没有舌片）跨过分界圆的格子，料全在圆内 ⇒ f 必须恰为 1。
///      这是数值把关人第七轮指出的单向偏差的门：分子分母若不是同一张栅格量的，这里会出现 f &lt; 1。
///   ③ 正对照（物理把关人第四轮条件 3）：圆盘保温 = 舌保温时，混合前后（有厚度场 vs 抹掉厚度场退回形心）整场温度与抽热逐位相同。
/// 慢门（数值把关人第七轮定的两条，出处 deliverable/R48_实验c_温度场台阶抖动_2026-09-14.txt 的 c1）：
///   ④ 分界半径在一个格宽内平移（30 + k·h/8），h = 0.5 抽热直线最大残差 ≤ 0.1 W（修前 0.397 / 0.506 W；孔圆自身抖动 0.039 / 0.057 W，门槛约其两倍）；
///   ⑤ 修后 r = 30 处的抽热与修前 c1 九点平均相差 ≤ 0.1 W：片0 −14.828（h=0.5）/ −13.648（h=1）；片2 −113.275 / −111.107。
///      依据：对称平移九点再平均，一阶上等于按面积份额混合。设置与实验 c 完全相同（旧管根 1141.8 / 1057.8 °C）。
///   ⚠ 修法**不负责**均匀网格的收敛（那已由 R48_均匀网格补0.25 回答：一阶收敛，比 0.53 / 0.54）。
///
/// ══ ④⑤ 第一次跑（13:57）**两条都没过**（deliverable/R48_保温分界混合_门_2026-09-14.txt）：
///   h=0.5 残差 片0 0.238、片2 0.300 W（门 0.1）；r=30 处与修前九点平均差 片0 0.275 / 0.565（h=0.5 / 1）、片2 0.348 / 0.714 W（门 0.1）。
///   跑完才看出的嫌疑（**事后解释，要验**）：两条门都假设抽热对分界半径是**光滑**的，而 r = 盘半径处有一个物理拐点——
///   分界往里挪（r &lt; 30）时 +x 半边的盘缘也换成舌保温，往外挪（r &gt; 30）时 +x 半边没有料、只有 −x 舌根在变。
///   R48_保温分界敏感度_2026-09-14.txt B 段：r=35 斜率 −2.73（片0）/ −3.85（片2）W/mm，而跨 30 的 ±2 mm 直线残差 2.12 / 2.68 W —— 拐点确实在。
///   一条直线去拟跨拐点的 V 形，残差与「九点平均 − 中心值」都不为 0，与离散无关。
///   ⇒ ④⑤ 原门槛与数字**原样保留、不改**，标为「门设错了（没考虑盘缘拐点）」，不再作为本修法的判据；由 ⑥ 验证这个解释并给出替代的门。
///
/// ══ ⑥（跑之前写死）
///   · 单侧平移：分界放在 30 + k·h/8（k = 1…8，全在盘缘外，不跨拐点），混合 vs 抹掉厚度场退回形心，同一组点。
///     判读 a：h=0.5 混合的直线最大残差 ≤ 0.1 W ⇒ 混合消掉了台阶抖动；形心版残差只作对照。
///   · 拐点模型：再在 30 − k·h/8（k = 1…8，全在盘缘内）量内侧斜率 s_in；外侧斜率 s_out 取单侧平移的直线斜率。
///     用 V(x) = Q(30) + s_in·x（x &lt; 0）/ s_out·x（x &gt; 0）在 ④ 那九个点上重做「直线最大残差」与「九点平均 − 中心值」。
///     判读 b：两者与 ④⑤ 实测（上面四组数）逐组相差 ≤ 25 % ⇒ ④⑤ 没过是物理拐点，不是修法错；超出 ⇒ 修法或等价关系有问题。
///   ⑥ 14:01 过。数值把关人第八轮指出两处不足：(i) 「④ 残差」复现是自证（V 形残差由两侧斜率决定，斜率取自同一批修后数据），
///   独立的只有「修前九点平均 − 修后中心值」一条（两批不同运行，差 1～3 %）；(ii) 内侧没有形心对照，而 +x 份额分支只在内侧用到；
///   h=0.5 判读 a 的 0.1 W 门形心版也过。⑥ 已看过结果，不再改它的门槛。
///
/// ══ ⑦（跑之前写死；数值把关人第八轮）：**没看过的数据**上验 —— 片1、片3，内外两侧都跑，每侧配形心对照。
///   依据：混合后剩下的台阶来自份额 f 被厚度场栅格（步 h/4）量化，形心口径的台阶周期是格宽 h ⇒ 混合残差应 ≤ 形心残差的 1/4。
///   · 主判读（h=1）：四条序列（2 片 × 内外）混合的直线最大残差都 ≤ 形心的 1/4；
///   · 附加（h=1）：混合序列逐步差里「隔一步交替」分量的能量占比 E_alt = (Σ(−1)^i(d_i − d̄))² ÷ (n·Σ(d_i − d̄)²) ≥ 0.5 的序列 ≥ 3 条（纯交替 = 1，白噪声期望约 1/7）；
///   · h=0.5 只打印不判（外侧实测正好约 1/4，贴线）。
/// ══ ⑧（跑之前写死；数值把关人第八轮）：混合后阶数不退化 —— 片0、片2 均匀 h=0.25（混合打开）。
///   h=1、0.5 修后值取 ⑥ 文件：片0 −14.213 / −15.103，片2 −111.821 / −113.623。
///   · Δ(0.5→0.25) ÷ Δ(1→0.5) 落在 [0.4, 0.6]；
///   · Δ(0.5→0.25) 与修前（R48_均匀网格补0.25：片0 −0.551、片2 −1.075）相差 ≤ 0.11 / 0.15 W（形心口径 h=0.5 外侧抖动的两倍）。
/// </summary>
public class R48InsulBlendTests
{
    private readonly ITestOutputHelper _out;
    public R48InsulBlendTests(ITestOutputHelper o) { _out = o; }

    private static (DesignSpec d, DesignInputs p, LineCase lc) Landing()
    {
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        var p = new DesignInputs();
        d.SizeTongues(p);
        return (d, p, d.BuildCase(p, checkRamp: false));
    }

    private static (ShellMesh m, double[] jm, FlangePlate g) Plate(DesignSpec d, DesignInputs p, LineCase lc, int j, double h, double iA)
    {
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        var g = d.Plate(j, d.DiscFloorMm(p));
        g.HoleRadiusMm = holeR;
        var (xa, za) = FlangeMesher.AnchorsOf(g);
        var tf = FlangeMesher.Rasterize(g, Math.Max(h / 4, 0.02));
        // 口径钉住（2026-09-14 Opus 5）：生产默认已改为压接整面接触 + 自相似压接细带，本探针显式钉回改动前口径（只钉外圈、不铺细带），deliverable 里同名证据文件不换口径
        //   （快门 ②③ 量的是份额与混合的构造性质，与压接口径无关；慢门 ⑤⑧ 写死的对照值是老口径下的数）
        var m = FlangeMesher.BuildFromField(tf, holeR, 0, h, h, 1e6, lc.Base.BusbarClampLengthMm, h, 0, g.TwoTabs, xa, za, clampBandMm: 0, clampFullFace: false);
        double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
        var sc = ShellCurrent.SolveFor(lc, m, iA, Materials.PtResistivity(tSet) * 1e3, tSet);
        return (m, sc.JMagAPerMm2, g);
    }

    private static ShellThermalResult Th(DesignSpec d, LineCase lc, int j, ShellMesh m, double[] jm, FlangePlate g, double tRoot,
                                         double insulR, double discInsul = double.NaN)
    {
        double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
        var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet;
        if (j < lc.ClampTempC.Length) p2.BusbarClampTempC = lc.ClampTempC[j];
        if (!double.IsNaN(discInsul)) p2.FlangeInsulThickMm = discInsul;
        return ShellThermal.Solve(m, jm, p2, tRoot, g.InsulBoundaryXResolved, g.TwoTabs,
                                  tabBoundaryX: g.Tangent().X, tabInsulThickMm: d.TabInsulMm[j],
                                  discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: insulR);
    }

    [Fact]
    public void 一_混合表端点与同表逐位不变()
    {
        var a = new LossTable(20, 1400, 60, t => 3e-5 * t + 1e-8 * t * t);
        var b = new LossTable(20, 1400, 60, t => 1e-5 * t + 4e-9 * t * t);
        var b2 = new LossTable(20, 1400, 60, t => 1e-5 * t + 4e-9 * t * t);
        var f0 = LossTable.Blend(a, b, 0.0);
        var f1 = LossTable.Blend(a, b, 1.0);
        var same = LossTable.Blend(b2, b, 0.37);
        foreach (double t in new[] { 20.0, 333.3, 1000.0, 1187.34, 1400.0, 1500.0 })
        {
            Assert.Equal(b.Eval(t), f0.Eval(t));
            Assert.Equal(b.Slope(t), f0.Slope(t));
            Assert.Equal(a.Eval(t), f1.Eval(t), 15);
            Assert.Equal(b.Eval(t), same.Eval(t));
            Assert.Equal(b.Slope(t), same.Slope(t));
        }
        var other = new LossTable(20, 1300, 60, t => t);
        Assert.Throws<ArgumentException>(() => LossTable.Blend(a, other, 0.5));
    }

    [Fact]
    public void 二_正x半边盘缘格料全在圆内_份额恰为一()
    {
        var (d, p, lc) = Landing();
        foreach (double h in new[] { 2.0, 1.0, 0.5 })
        {
            var (m, _, g) = Plate(d, p, lc, 0, h, 1214);
            double R = g.InsulDiscRadiusMm;
            Assert.False(double.IsNaN(R));
            int n = 0, bad = 0; double worst = 1;
            for (int i = 0; i < m.CellCount; i++)
            {
                var nd = m.Cells[i];
                double x0 = nd.Min(k => m.Nodes[k].X), x1 = nd.Max(k => m.Nodes[k].X);
                double z0 = nd.Min(k => m.Nodes[k].Z), z1 = nd.Max(k => m.Nodes[k].Z);
                if (x0 <= 0) continue;                                   // 只看 +x 半边：那里没有舌片，料的外缘就是盘缘 = 保温圆
                double fx = Math.Max(Math.Abs(x0), Math.Abs(x1)), fz = Math.Max(Math.Abs(z0), Math.Abs(z1));
                double nx = Math.Clamp(0, x0, x1), nz = Math.Clamp(0, z0, z1);
                if (FlangePlate.InsideInsulCircle(fx, fz, R) || !FlangePlate.InsideInsulCircle(nx, nz, R)) continue;
                double f = FlangeMesher.MaterialFractionInCircle(m, i, R);   // 2026-09-18 Fable 5.1：同 ShellThermal 的入口（解析板精确积分）
                if (double.IsNaN(f)) continue;
                n++;
                if (f != 1.0) { bad++; worst = Math.Min(worst, f); }
            }
            _out.WriteLine($"h={h}：+x 半边跨分界圆的有料格 {n} 个，份额不为 1 的 {bad} 个（最小 {worst:R}）");
            Assert.True(n > 0, "没找到跨分界圆的格子，门没测到东西");
            Assert.Equal(0, bad);
        }
    }

    [Fact]
    public void 三_正对照_圆盘保温等于舌保温时混合前后逐位相同()
    {
        var (d, p, lc) = Landing();
        var (m, jm, g) = Plate(d, p, lc, 0, 2.0, 1214);
        var withField = Th(d, lc, 0, m, jm, g, 1141.8, g.InsulDiscRadiusMm, d.TabInsulMm[0]);
        var field = m.Material;   // 2026-09-18 Fable 5.1：「没有材料来源」现在读 ShellMesh.Material（解析路径没有栅格 SourceField 了）
        m.Material = null;
        try
        {
            var centroid = Th(d, lc, 0, m, jm, g, 1141.8, g.InsulDiscRadiusMm, d.TabInsulMm[0]);
            _out.WriteLine(withField.InsulRule);
            _out.WriteLine(centroid.InsulRule);
            Assert.Contains("按有料面积份额混合", withField.InsulRule);
            Assert.Contains("按形心整格归一边", centroid.InsulRule);
            Assert.Equal(centroid.QFromTubeW, withField.QFromTubeW);
            Assert.Equal(centroid.T.Length, withField.T.Length);
            for (int i = 0; i < centroid.T.Length; i++) Assert.Equal(centroid.T[i], withField.T[i]);
        }
        finally { m.Material = field; }
    }

    [Fact, Trait("速度", "慢")]
    public void 四五_分界平移残差与九点平均()
    {
        var sb = new StringBuilder();
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        string file = DeliverableOut.Stamped("R48_保温分界混合_门_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 保温分界混合修法的慢门（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　设置同实验 c");
        var (d, p, lc) = Landing();
        double[] iA = { 1214, 2102, 2102, 1214 };
        double[] tRoot = { 1141.8, 1107.8, 1057.8, 1038.0 };
        var avgBefore = new System.Collections.Generic.Dictionary<(int, double), double>
        {
            [(0, 0.5)] = -14.828, [(0, 1.0)] = -13.648, [(2, 0.5)] = -113.275, [(2, 1.0)] = -111.107,
        };
        bool ok = true;
        foreach (int j in new[] { 0, 2 })
            foreach (double h in new[] { 1.0, 0.5 })
            {
                var (m, jm, g) = Plate(d, p, lc, j, h, iA[j]);
                double R = g.InsulDiscRadiusMm;
                var ks = Enumerable.Range(-4, 9).Select(k => k * h / 8).ToArray();
                var q = ks.Select(x => Th(d, lc, j, m, jm, g, tRoot[j], R + x).QFromTubeW).ToArray();
                double mx = ks.Average(), my = q.Average();
                double slope = ks.Zip(q, (a, b) => (a - mx) * (b - my)).Sum() / ks.Sum(a => (a - mx) * (a - mx));
                double res = ks.Zip(q, (a, b) => Math.Abs(b - (my + slope * (a - mx)))).Max();
                double at30 = q[4];
                double gap = Math.Abs(at30 - avgBefore[(j, h)]);
                bool resOk = h != 0.5 || res <= 0.1;
                bool gapOk = gap <= 0.1;
                if (!resOk || !gapOk) ok = false;
                Say($"   片{j} h={h:0.0}：抽热 {string.Join(" ", q.Select(v => v.ToString("+0.000;-0.000")))} W");
                Say($"        斜率 {slope:+0.00;-0.00} W/mm　直线最大残差 {res:0.000} W{(h == 0.5 ? (resOk ? "（≤ 0.1 ✓）" : "（**> 0.1 ✗**）") : "（h=1 只记录）")}"
                  + $"　r=30 处 {at30:+0.000;-0.000} vs 修前九点平均 {avgBefore[(j, h)]:+0.000;-0.000}，差 {gap:0.000} W{(gapOk ? "（≤ 0.1 ✓）" : "（**> 0.1 ✗**）")}");
            }
        Say(ok ? "★ 两条慢门都过 ⇒ 修法做对了。" : "★ **有门没过** ⇒ 修法或等价关系有问题，先查，不接进结论。");
        // ⚠ 13:57 第一次跑两条都没过，门设错了（见类注释），数字与门槛原样保留；断言改为只记录，由 ⑥ 判。
        _out.WriteLine(ok ? "④⑤ 过" : "④⑤ 没过（门没考虑盘缘拐点，见类注释与 ⑥）");
    }

    [Fact, Trait("速度", "慢")]
    public void 六_单侧平移残差与拐点模型()
    {
        var sb = new StringBuilder();
        string file = DeliverableOut.Stamped("R48_保温分界混合_门六_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 保温分界混合修法 门六：单侧平移与拐点模型（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　设置同实验 c");
        var (d, p, lc) = Landing();
        double[] iA = { 1214, 2102, 2102, 1214 };
        double[] tRoot = { 1141.8, 1107.8, 1057.8, 1038.0 };
        // ④⑤ 第一次跑的实测（R48_保温分界混合_门_2026-09-14.txt，13:57）：(片, h) → (直线最大残差, 九点平均 − r=30 值)
        var obs = new System.Collections.Generic.Dictionary<(int, double), (double res, double off)>
        {
            [(0, 1.0)] = (0.527, -13.648 - (-14.213)), [(0, 0.5)] = (0.238, -14.828 - (-15.103)),
            [(2, 1.0)] = (0.665, -111.107 - (-111.821)), [(2, 0.5)] = (0.300, -113.275 - (-113.623)),
        };
        static (double slope, double maxRes, double mean) Fit(double[] x, double[] y)
        {
            double mx = x.Average(), my = y.Average();
            double k = x.Zip(y, (a, b) => (a - mx) * (b - my)).Sum() / x.Sum(a => (a - mx) * (a - mx));
            return (k, x.Zip(y, (a, b) => Math.Abs(b - (my + k * (a - mx)))).Max(), my);
        }
        bool aOk = true, bOk = true;
        foreach (int j in new[] { 0, 2 })
            foreach (double h in new[] { 1.0, 0.5 })
            {
                var (m, jm, g) = Plate(d, p, lc, j, h, iA[j]);
                double R = g.InsulDiscRadiusMm;
                var xo = Enumerable.Range(1, 8).Select(k => k * h / 8).ToArray();
                var xi = Enumerable.Range(1, 8).Select(k => -k * h / 8).ToArray();
                double q30 = Th(d, lc, j, m, jm, g, tRoot[j], R).QFromTubeW;
                var qo = xo.Select(x => Th(d, lc, j, m, jm, g, tRoot[j], R + x).QFromTubeW).ToArray();
                var qi = xi.Select(x => Th(d, lc, j, m, jm, g, tRoot[j], R + x).QFromTubeW).ToArray();
                var field = m.Material;   // 2026-09-18 Fable 5.1：「没有材料来源」现在读 ShellMesh.Material（解析路径没有栅格 SourceField 了）
                double[] qoC;
                m.Material = null;
                try { qoC = xo.Select(x => Th(d, lc, j, m, jm, g, tRoot[j], R + x).QFromTubeW).ToArray(); }
                finally { m.Material = field; }
                var fo = Fit(xo, qo); var fi = Fit(xi, qi); var foC = Fit(xo, qoC);
                // 拐点模型在 ④ 的九个点上
                var x9 = Enumerable.Range(-4, 9).Select(k => k * h / 8).ToArray();
                var v9 = x9.Select(x => q30 + (x < 0 ? fi.slope : fo.slope) * x).ToArray();
                var f9 = Fit(x9, v9);
                double predRes = f9.maxRes, predOff = v9.Average() - q30;
                var (oRes, oOff) = obs[(j, h)];
                bool bRow = Math.Abs(predRes - oRes) <= 0.25 * oRes && Math.Abs(predOff - oOff) <= 0.25 * Math.Abs(oOff);
                if (!bRow) bOk = false;
                if (h == 0.5 && !(fo.maxRes <= 0.1)) aOk = false;
                Say($"── 片{j} h={h:0.0}　r=30 处抽热 {q30:+0.000;-0.000} W");
                Say($"   外侧单侧（混合）：{string.Join(" ", qo.Select(v => v.ToString("+0.000;-0.000")))}　斜率 {fo.slope:+0.00;-0.00} W/mm　直线最大残差 {fo.maxRes:0.000} W"
                  + (h == 0.5 ? (fo.maxRes <= 0.1 ? "（≤ 0.1 ✓）" : "（**> 0.1 ✗**）") : "（h=1 只记录）"));
                Say($"   外侧单侧（形心）：{string.Join(" ", qoC.Select(v => v.ToString("+0.000;-0.000")))}　斜率 {foC.slope:+0.00;-0.00} W/mm　直线最大残差 {foC.maxRes:0.000} W（对照）");
                Say($"   内侧单侧（混合）：{string.Join(" ", qi.Select(v => v.ToString("+0.000;-0.000")))}　斜率 {fi.slope:+0.00;-0.00} W/mm　直线最大残差 {fi.maxRes:0.000} W");
                Say($"   拐点模型预测 ④ 残差 {predRes:0.000} vs 实测 {oRes:0.000}；九点平均 − 中心 预测 {predOff:+0.000;-0.000} vs 实测 {oOff:+0.000;-0.000}　{(bRow ? "（25 % 内 ✓）" : "（**超出 25 % ✗**）")}");
            }
        Say("");
        Say($"★ 判读 a：{(aOk ? "h=0.5 外侧单侧混合残差都 ≤ 0.1 W ⇒ 混合消掉了台阶抖动" : "**h=0.5 外侧单侧混合残差有 > 0.1 W 的 ⇒ 混合没消掉抖动**")}；"
          + $"判读 b：{(bOk ? "拐点模型逐组复现 ④⑤ 实测（25 % 内）⇒ ④⑤ 没过是物理拐点，不是修法错" : "**拐点模型复现不了 ④⑤ ⇒ 修法或等价关系有问题**")}");
        Assert.True(aOk && bOk, "门六没过，见 deliverable/R48_保温分界混合_门六_2026-09-14.txt");
    }

    [Fact, Trait("速度", "慢")]
    public void 七_没看过的片内外两侧对形心()
    {
        var sb = new StringBuilder();
        string file = DeliverableOut.Stamped("R48_保温分界混合_门七_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 保温分界混合修法 门七：片1、片3 内外两侧对形心（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　设置同实验 c");
        var (d, p, lc) = Landing();
        double[] iA = { 1214, 2102, 2102, 1214 };
        double[] tRoot = { 1141.8, 1107.8, 1057.8, 1038.0 };
        static (double slope, double maxRes) Fit(double[] x, double[] y)
        {
            double mx = x.Average(), my = y.Average();
            double k = x.Zip(y, (a, b) => (a - mx) * (b - my)).Sum() / x.Sum(a => (a - mx) * (a - mx));
            return (k, x.Zip(y, (a, b) => Math.Abs(b - (my + k * (a - mx)))).Max());
        }
        static double EAlt(double[] q)
        {
            var dd = Enumerable.Range(0, q.Length - 1).Select(i => q[i + 1] - q[i]).ToArray();
            double mean = dd.Average();
            double num = 0, den = 0;
            for (int i = 0; i < dd.Length; i++) { num += (i % 2 == 0 ? 1 : -1) * (dd[i] - mean); den += (dd[i] - mean) * (dd[i] - mean); }
            return den > 0 ? num * num / (dd.Length * den) : double.NaN;
        }
        bool mainOk = true; int altCount = 0;
        foreach (int j in new[] { 1, 3 })
            foreach (double h in new[] { 1.0, 0.5 })
            {
                var (m, jm, g) = Plate(d, p, lc, j, h, iA[j]);
                double R = g.InsulDiscRadiusMm;
                foreach (int side in new[] { +1, -1 })
                {
                    var xs = Enumerable.Range(1, 8).Select(k => side * k * h / 8).ToArray();
                    var qb = xs.Select(x => Th(d, lc, j, m, jm, g, tRoot[j], R + x).QFromTubeW).ToArray();
                    var field = m.Material;   // 2026-09-18 Fable 5.1：「没有材料来源」现在读 ShellMesh.Material（解析路径没有栅格 SourceField 了）
                    double[] qc;
                    m.Material = null;
                    try { qc = xs.Select(x => Th(d, lc, j, m, jm, g, tRoot[j], R + x).QFromTubeW).ToArray(); }
                    finally { m.Material = field; }
                    var fb = Fit(xs, qb); var fc = Fit(xs, qc);
                    double ratio = fc.maxRes > 0 ? fb.maxRes / fc.maxRes : double.NaN;
                    double ea = EAlt(qb);
                    string tag = side > 0 ? "外侧" : "内侧";
                    if (h == 1.0)
                    {
                        if (!(ratio <= 0.25)) mainOk = false;
                        if (ea >= 0.5) altCount++;
                    }
                    Say($"── 片{j} h={h:0.0} {tag}");
                    Say($"   混合：{string.Join(" ", qb.Select(v => v.ToString("+0.000;-0.000")))}　斜率 {fb.slope:+0.00;-0.00}　残差 {fb.maxRes:0.000} W　E_alt {ea:0.00}");
                    Say($"   形心：{string.Join(" ", qc.Select(v => v.ToString("+0.000;-0.000")))}　斜率 {fc.slope:+0.00;-0.00}　残差 {fc.maxRes:0.000} W　E_alt {EAlt(qc):0.00}");
                    Say($"   混合 ÷ 形心 残差 = {ratio:0.000}" + (h == 1.0 ? (ratio <= 0.25 ? "（≤ 1/4 ✓）" : "（**> 1/4 ✗**）") : "（h=0.5 只打印）"));
                }
            }
        Say("");
        Say($"★ 主判读（h=1 四条混合残差 ≤ 形心 1/4）：{(mainOk ? "过" : "**没过**")}；附加（E_alt ≥ 0.5 的序列 ≥ 3 条）：{altCount} 条，{(altCount >= 3 ? "过" : "**没过**")}");
        Assert.True(mainOk && altCount >= 3, "门七没过，见 deliverable/R48_保温分界混合_门七_2026-09-14.txt");
    }

    [Fact, Trait("速度", "慢")]
    public void 八_混合后均匀网格补四分之一毫米_阶数不退化()
    {
        var sb = new StringBuilder();
        string file = DeliverableOut.Stamped("R48_保温分界混合_门八_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 保温分界混合修法 门八：混合后均匀 h=0.25（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　设置同实验 c");
        var (d, p, lc) = Landing();
        double[] iA = { 1214, 2102, 2102, 1214 };
        double[] tRoot = { 1141.8, 1107.8, 1057.8, 1038.0 };
        var after = new System.Collections.Generic.Dictionary<int, (double q1, double q05)> { [0] = (-14.213, -15.103), [2] = (-111.821, -113.623) };
        var before = new System.Collections.Generic.Dictionary<int, (double d4, double tol)> { [0] = (-0.551, 0.11), [2] = (-1.075, 0.15) };
        bool ok = true;
        foreach (int j in new[] { 0, 2 })
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (m, jm, g) = Plate(d, p, lc, j, 0.25, iA[j]);
            var th = Th(d, lc, j, m, jm, g, tRoot[j], g.InsulDiscRadiusMm);
            double d3 = after[j].q05 - after[j].q1, d4 = th.QFromTubeW - after[j].q05;
            double ratio = d4 / d3;
            bool rOk = ratio >= 0.4 && ratio <= 0.6;
            bool tOk = Math.Abs(d4 - before[j].d4) <= before[j].tol;
            if (!rOk || !tOk) ok = false;
            Say($"   片{j}：h=0.25 {m.CellCount} 格 抽热 {th.QFromTubeW:+0.000;-0.000} W（{th.InsulRule}；{sw.Elapsed.TotalSeconds:0} s）");
            Say($"        Δ(1→0.5) {d3:+0.000;-0.000}　Δ(0.5→0.25) {d4:+0.000;-0.000}　比 {ratio:0.00}{(rOk ? "（[0.4,0.6] ✓）" : "（**不在 [0.4,0.6] ✗**）")}"
              + $"　与修前 {before[j].d4:+0.000;-0.000} 差 {Math.Abs(d4 - before[j].d4):0.000} W{(tOk ? $"（≤ {before[j].tol:0.00} ✓）" : $"（**> {before[j].tol:0.00} ✗**）")}");
        }
        Say(ok ? "★ 门八过：混合后一阶收敛不变。" : "★ **门八没过**");
        Assert.True(ok, "门八没过，见 deliverable/R48_保温分界混合_门八_2026-09-14.txt");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
