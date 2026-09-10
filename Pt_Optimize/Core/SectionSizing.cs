using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **截面电流密度：按 J = 10 定尺寸，终验全体 &lt; 11**（2026-09-08，用户给的设计因果链第 ②／④ 步）。
///
/// 用户原话：「在 20 °C/h 下所须通过铂金管的电流是多少 → 再去决定铂金法兰大小与尺寸（r1/t1/r2/t2）与舌片横截面积
/// [按 J=10 为限制] → 分析温场与电场定孔 → 最后复核：核算电流通过**截面**的电流密度，全体必须小于 11。」
///
/// ══ 什么是「截面」
/// 电流路径上每一个**必经**的切口：
///   · 舌片：沿舌轴每一处 x 的横截面（宽 × 厚），开孔处扣掉孔的弦长；压接段（铜排短接）不算
///   · 舌盘交界：切点处的弦 × 厚
///   · 圆盘：绕管孔的每一圈 r（周长 × 该级厚度），减重槽带扣掉槽的弧长
/// 截面电流密度 J = I_设计 / A_截面，**闭式、与网格无关** ⇒ 当场判得了。
///
/// ══ 舌片厚度**不是旋钮**（用户 2026-09-08 纠正，R11）
/// 「舌片厚度是截面积 I/10 ÷ 舌宽」⇒ 舌片厚 = I /(J_设计 × 舌片最窄有效宽)，闭式一步（<see cref="TongueThickMm"/>），
/// 与圆盘基板、各级台阶**解耦**：圆盘各级按各自的截面（各圈周长 × 厚）定，舌片按自己的截面定。
/// 在此之前舌片厚 = 圆盘基板厚，一根旋钮管两处 ⇒ 舌片截面把整片圆盘一起抬厚（W08 共用片要 4.26 mm ⇒ ⑥ 盖不住）。
///
/// ══ 它推翻了什么（按用户第 8 条：可以推翻不当的逻辑，要说出来）
/// 前任把「法兰 J」定义成电流场的**逐点峰值**（凹角处 37 A/mm²，随网格涨、无收敛平台），追网格到收敛才肯判
/// ⇒ 永远「判不了」，挡住每一档交付。逐点峰值是局部发热问题，由温度场（含横向导热）与熔化门管；
/// 尺寸规则看的是截面。场峰值现在降为诊断量，给第 ③ 步（电流密度低处定孔）用。
///
/// ══ 单调性
/// 所有截面积都正比于所在部位的厚度（舌片 t_舌、各级 t×倍率、弦 t）⇒ 给定电流，J 对厚度严格递减 ⇒
/// 圆盘基板下界 = 当前厚度 × max_圆盘侧截面(J/10)，舌片厚 = I/(10·w_min)，都闭式一步到位，是约束盒下角的来源。
/// 孔径与槽张角**减小**截面 ⇒ 它们的上界也由这里给（比桥宽那条更紧的那个生效）。
/// </summary>
public static class SectionSizing
{
    /// <summary>
    /// 设计用电流密度 A/mm² 的**预设值**（用户 2026-09-08：按 J=10 为限制定尺寸；2026-09-09：「J 让工程师设定，
    /// 实况风险工程师承担；J 预设值为 10」）。实际用的 J 在 <see cref="DesignSpec.JDesignAPerMm2"/>（① 输入的控件），这里只是默认。
    /// </summary>
    public const double JDesignAPerMm2 = 10.0;
    /// <summary>终验限值 A/mm² 的预设值（= 预设 J + 1 = 11）。实际限值由 <see cref="JCheckOf"/> 按设定 J 算。</summary>
    public const double JCheckAPerMm2 = 11.0;
    /// <summary>计算极限值 = 设定值 + 1（用户 2026-09-09：「J 是设定值，J+1 是计算极限值」）。终验全体截面 J 必须小于它。</summary>
    public static double JCheckOf(double jDesign) => jDesign + 1.0;

    /// <summary>一个截面：在哪、面积、电流密度；<paramref name="OnTab"/> = 在舌片上（随舌片厚变，不随基板变）。</summary>
    public readonly record struct Cut(string Where, double AreaMm2, double JAPerMm2, bool OnTab = false);

    /// <summary>
    /// 舌片沿 x 的**有效宽度**（扣掉孔的弦；压接段不算）。返回 (x, 宽)，宽 ≤ 0 表示被孔切断。
    /// 舌片截面与舌片厚度两处都从这一份采样取，不各写一遍。
    /// </summary>
    public static List<(double X, double WidthMm)> TabWidths(FlangePlate g, double clampLenMm = 0)
    {
        var res = new List<(double, double)>();
        var (xT, _) = g.Tangent();
        double x0 = g.TabTipXMm + Math.Max(0, clampLenMm), x1 = xT;
        if (!(x1 > x0 + 1e-9)) return res;
        var xs = new SortedSet<double>();
        const int N = 240;
        for (int i = 0; i <= N; i++) xs.Add(x0 + (x1 - x0) * i / N);
        foreach (var h in g.TabHoles) xs.Add(Math.Clamp(h.XMm, x0, x1));
        foreach (double x in xs)
        {
            double hw = g.HalfWidth(x);
            double w = 2 * hw;
            foreach (var h in g.TabHoles) w -= ChordMm(h, x, hw);        // 扣掉孔的弦
            w -= DiscCutChordMm(g, x, hw);                                // R23：圆盘槽／长椭圆落到舌根时，舌片这一处也少了这段
            res.Add((x, Math.Max(0, w)));
        }
        return res;
    }

    /// <summary>
    /// ★ R23（用户 2026-09-09：叉口用「直椭圆 + 弯椭圆」或「圆角三角」拼，不加新几何）：
    /// 圆盘切口（弯槽 <see cref="FlangePlate.DiscSlots"/>／长椭圆 <see cref="FlangePlate.DiscCutHoles"/>）落在舌根时，
    /// 在横坐标 x 处、|z| ≤ halfW 内被它们挖掉的总长。按形状自己的 Contains 数值量（400 格 + 端点细分），与 <see cref="ChordMm"/> 同一手法；
    /// 没有圆盘切口时恒为 0 ⇒ 旧数逐位不变。
    /// </summary>
    public static double DiscCutChordMm(FlangePlate g, double x, double halfW)
    {
        if ((g.DiscSlots.Length == 0 && g.DiscCutHoles.Length == 0) || !(halfW > 0)) return 0;
        bool In(double z)
        {
            foreach (var s in g.DiscSlots) if (s.Contains(x, z)) return true;
            foreach (var h in g.DiscCutHoles) if (h.Contains(x, z)) return true;
            return false;
        }
        const int N = 400;
        double step = 2 * halfW / N, total = 0;
        bool prev = In(-halfW);
        for (int i = 1; i <= N; i++)
        {
            double z = -halfW + i * step;
            bool cur = In(z);
            if (cur && prev) total += step;
            else if (cur != prev)
            {
                double a = z - step, b = z;                           // 端点二分，把格子误差压到 1e-4 mm
                for (int k = 0; k < 20; k++) { double m = 0.5 * (a + b); if (In(m) == prev) a = m; else b = m; }
                total += prev ? (a - (z - step)) : (z - b);
            }
            prev = cur;
        }
        return Math.Min(total, 2 * halfW);
    }

    /// <summary>
    /// 孔在 x 处占掉的弦长 mm（限制在 |z| ≤ halfW 内）。
    /// 圆／椭圆且不转角：闭式 2R√(1−(dx/(R·asp))²)（与 2026-09-08 逐位相同）；
    /// 圆角三角／方或转了角（R13）：形状是凸的（凸多边形 ⊕ 圆盘再仿射），与竖线的交是**一段**区间 ——
    /// 先粗采样找到孔内点，再对两端各二分到 1e-6 mm。闭式几何，不解场。
    /// </summary>
    public static double ChordMm(FlangePlate.TabHole h, double x, double halfW)
    {
        double asp = Math.Max(1e-9, h.AspectXZ);
        if (h.Sides < 3 || h.CornerFrac >= 0.999)
        {
            if (Math.Abs(h.RotDeg) < 1e-12)
            {
                double dx = (x - h.XMm) / (h.RMm * asp);
                return Math.Abs(dx) < 1 ? 2 * h.RMm * Math.Sqrt(1 - dx * dx) : 0;
            }
        }
        double rb = h.RMm * Math.Max(1.0, asp);                       // 外包半径（转角后最远也就这么远）
        if (Math.Abs(x - h.XMm) > rb) return 0;
        double zLo = Math.Max(-halfW, h.ZMm - rb), zHi = Math.Min(halfW, h.ZMm + rb);
        if (!(zHi > zLo)) return 0;
        const int N = 400;
        int first = -1, last = -1;
        for (int i = 0; i <= N; i++)
        {
            double z = zLo + (zHi - zLo) * i / N;
            if (!h.Contains(x, z)) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return 0;
        double step = (zHi - zLo) / N;
        double a = zLo + first * step, b = zLo + last * step;
        // 两端二分到边界（凸 ⇒ 单区间）
        double aOut = Math.Max(zLo, a - step), bOut = Math.Min(zHi, b + step);
        for (int k = 0; k < 40 && a - aOut > 1e-6; k++) { double m = 0.5 * (a + aOut); if (h.Contains(x, m)) a = m; else aOut = m; }
        for (int k = 0; k < 40 && bOut - b > 1e-6; k++) { double m = 0.5 * (b + bOut); if (h.Contains(x, m)) b = m; else bOut = m; }
        return Math.Max(0, b - a);
    }

    /// <summary>舌片最窄的有效宽度 mm（压接段之外；没有舌片段时 NaN）。</summary>
    public static double TabMinWidthMm(FlangePlate g, double clampLenMm = 0)
    {
        var ws = TabWidths(g, clampLenMm);
        return ws.Count == 0 ? double.NaN : ws.Min(p => p.WidthMm);
    }

    /// <summary>
    /// ★★★★★ **舌片厚度**（用户 2026-09-08：不是旋钮，= I/10 ÷ 舌宽）：
    /// t_舌 = I_设计 /(J_设计 × 舌片最窄有效宽)。闭式；开孔把弦扣掉之后宽变窄，舌片就得更厚。
    /// 电流为 0 或没有舌片段时返回 NaN（不给一个看起来正常的数）。
    /// </summary>
    public static double TongueThickMm(FlangePlate g, double currentA, double clampLenMm = 0,
                                       double jDesign = JDesignAPerMm2)
    {
        if (!(currentA > 0)) return double.NaN;
        double wMin = TabMinWidthMm(g, clampLenMm);
        if (double.IsNaN(wMin) || wMin <= 1e-9) return double.PositiveInfinity;   // 被孔切断：多厚都不够
        return currentA / (jDesign * wMin);
    }

    /// <summary>
    /// 全部必经截面。<paramref name="currentA"/> 是这一片的设计电流（共用片已矢量合成）。
    /// <paramref name="clampLenMm"/>：舌端压接段长度，段内由铜排短接、不算截面。
    /// </summary>
    public static List<Cut> Cuts(FlangePlate g, double currentA, double clampLenMm = 0)
    {
        var cuts = new List<Cut>();
        if (!(currentA > 0)) return cuts;
        double tTab = double.IsNaN(g.TabThicknessMm) ? g.ThicknessMm : g.TabThicknessMm;
        var (xT, hwT) = g.Tangent();

        // ── 舌片：从压接段末端到切点，沿 x 采样（孔心处必采）
        foreach (var (x, w) in TabWidths(g, clampLenMm))
        {
            if (w <= 1e-9) { cuts.Add(new Cut($"舌片 x={x:0.#}（被孔切断）", 0, double.PositiveInfinity, true)); continue; }
            double tx = TabThicknessAt(g, x, tTab);                    // R29：带内取臂厚
            double a = w * tx;
            cuts.Add(new Cut(g.InTabArm(x) ? $"舌片 x={x:0.#}（叉臂 {tx:0.00}）" : $"舌片 x={x:0.#}", a, currentA / a, true));
        }

        // ── 舌盘交界：切点竖线 x = xT 上舌片电流的**必经**切口（2026-09-09 修正，审查抓到两处不当）：
        //   ① 原来用整条弦 2·hwT × 圆盘侧厚，没扣管孔 —— 盘半径 = 舌半宽时切点在 x=0，那条弦有 51.6 mm 穿过管腔（没有料）；
        //   ② 但也不能只剩两条边条：|xT| < 孔半径时，舌片脚印里有一段管孔边（焊弧），电流就在那里经焊缝进管壁，
        //      不必绕到边条 —— 那段焊弧的板厚截面（弧长 × 孔边厚）同样是舌片电流的出口。
        //   ⇒ 切口面积 = 边条（弦扣掉管孔弦，圆盘侧厚）+ 焊弧（x < xT 且 |z| ≤ hwT 那段孔边弧长 × 孔边处板厚）。
        //   |xT| ≥ 孔半径时弦不穿孔、焊弧不在脚印里 ⇒ 退回原式 2·hwT·t（逐位相同）。
        {
            double aH = g.HoleRadiusMm;
            double chordHalf = Math.Abs(xT) < aH ? Math.Sqrt(aH * aH - xT * xT) : 0;     // 弦穿过管腔的半长
            double stripW = 2 * Math.Max(0, hwT - chordHalf);                              // 两条边条总宽
            // R23：横跨切点的舌孔（叉口开到舌根）与落在舌根的圆盘切口，在切点这条线上也要扣掉
            double cutAtT = 0;
            foreach (var h in g.TabHoles) cutAtT += ChordMm(h, xT, hwT);
            cutAtT += DiscCutChordMm(g, xT, hwT);
            stripW = Math.Max(0, stripW - cutAtT);
            double zMid = Math.Min(hwT, chordHalf + 0.5 * Math.Max(0, hwT - chordHalf));    // 边条中点（在圆盘侧：x = xT 不算舌片）
            double strips = stripW * Math.Max(g.ThicknessAt(xT, zMid), 1e-9);
            double arcLen = 0, tEdge = 0;
            if (Math.Abs(xT) < aH)
            {
                // 孔边圆弧上 x < xT 的那段以 −x 轴为中心、半角 th0 = acos(|xT|/a)；再限在舌片脚印 |z| ≤ hwT 里：半角 ≤ asin(hwT/a)
                double th0 = Math.Acos(Math.Min(1.0, Math.Abs(xT) / aH));
                double thMax = Math.Asin(Math.Min(1.0, hwT / aH));
                arcLen = 2 * Math.Min(th0, thMax) * aH;
                tEdge = Math.Max(g.ThicknessAt(-aH - 1e-6, 0), 1e-9);                      // 孔边、舌片侧的板厚（舌片厚）
            }
            double a = strips + arcLen * tEdge;
            cuts.Add(new Cut(Math.Abs(xT) < aH ? $"舌盘交界（边条 {2 * Math.Max(0, hwT - chordHalf):0.#} mm + 焊弧 {arcLen:0.#} mm）x={xT:0.#}" : $"舌盘交界弦 x={xT:0.#}",
                             a, a > 1e-9 ? currentA / a : double.PositiveInfinity));
        }

        // ── 圆盘：绕管孔每一圈（焊脚之外到盘缘），槽带扣掉槽的弧长
        {
            double rIn = g.HoleRadiusMm + Math.Max(0, g.WeldFilletLegMm), rOut = g.DiscRadiusMm;
            if (rOut > rIn + 1e-9)
            {
                var rs = new SortedSet<double>();
                const int M = 120;
                for (int i = 0; i <= M; i++) rs.Add(rIn + (rOut - rIn) * i / M);
                foreach (double r in g.DiscStepRadiiMm) if (r > rIn && r < rOut) { rs.Add(r - 1e-6); rs.Add(r + 1e-6); }
                foreach (var s in g.DiscSlots) { if (s.RInMm > rIn && s.RInMm < rOut) rs.Add(s.RInMm + 1e-6); if (s.ROutMm > rIn && s.ROutMm < rOut) rs.Add(s.ROutMm - 1e-6); }
                foreach (var h in g.DiscCutHoles)
                {
                    double rc = Math.Sqrt(h.XMm * h.XMm + h.ZMm * h.ZMm), rb = h.RMm * Math.Max(1.0, h.AspectXZ);
                    if (rc - rb > rIn && rc - rb < rOut) rs.Add(rc - rb + 1e-6);
                    if (rc + rb > rIn && rc + rb < rOut) rs.Add(rc + rb - 1e-6);
                    if (rc > rIn && rc < rOut) rs.Add(rc);
                }
                foreach (double r in rs)
                {
                    double arc = 2 * Math.PI * r;
                    foreach (var s in g.DiscSlots)
                        if (r >= s.RInMm && r <= s.ROutMm) arc -= r * s.SpanDeg * Math.PI / 180.0;
                    // ★ 圆盘上的直孔（长椭圆，R13）：这一圈落在孔里的弧长按角度扣（采样 + 二分到边界）
                    foreach (var h in g.DiscCutHoles) arc -= r * ArcInsideRad(h, r);
                    // ★ 厚度取 +z 轴上的点 (0, r)：它在圆盘上、不在舌片上（舌片在 −x 侧，onTab 判 x < 切点）。
                    //   2026-09-09 审查抓到：原来取 (−r, 0)，R11 解耦后那一点落在舌片上 ⇒ 各圈读成舌片厚（盘半径 = 舌半宽时全部读错）。
                    double t = Math.Max(g.ThicknessAt(0, r), 1e-9);
                    if (arc <= 1e-9) { cuts.Add(new Cut($"圆盘 r={r:0.#}（被槽切断）", 0, double.PositiveInfinity)); continue; }
                    double a = arc * t;
                    cuts.Add(new Cut($"圆盘 r={r:0.#}", a, currentA / a));
                }
            }
        }
        return cuts;
    }

    /// <summary>
    /// 半径 r 的圆周落在孔 <paramref name="h"/> 里的总弧度。圆与凸区域的交最多两段 ——
    /// 先按 0.5° 采样找进出，再对每个过渡二分到 1e-6 rad。闭式几何，不解场。
    /// </summary>
    public static double ArcInsideRad(FlangePlate.TabHole h, double r)
    {
        double rc = Math.Sqrt(h.XMm * h.XMm + h.ZMm * h.ZMm), rb = h.RMm * Math.Max(1.0, h.AspectXZ);
        if (r < rc - rb - 1e-12 || r > rc + rb + 1e-12) return 0;
        const int N = 720;
        double step = 2 * Math.PI / N;
        var inside = new bool[N];
        for (int i = 0; i < N; i++) { double t = i * step; inside[i] = h.Contains(r * Math.Cos(t), r * Math.Sin(t)); }
        double total = 0;
        for (int i = 0; i < N; i++)
        {
            int j = (i + 1) % N;
            if (inside[i] == inside[j]) { if (inside[i]) total += step; continue; }
            // 过渡：在 [i·step, (i+1)·step] 里二分找边界，边界之前/之后归各自
            double a = i * step, b = (i + 1) * step;
            for (int k = 0; k < 40 && b - a > 1e-6; k++)
            {
                double m = 0.5 * (a + b);
                bool im = h.Contains(r * Math.Cos(m), r * Math.Sin(m));
                if (im == inside[i]) a = m; else b = m;
            }
            total += inside[i] ? (a - i * step) : ((i + 1) * step - b);
        }
        return Math.Min(total, 2 * Math.PI);
    }

    /// <summary>
    /// ★ 任意圆盘挖料形状的张角上界（按 J = 10）：给一个「张角 → 板」的造板函数，二分找**圆盘各圈** J 都 ≤ 10 的最大张角
    /// （面积随张角单调增 ⇒ 圈上剩余弧单调减 ⇒ 二分成立）。弯椭圆槽有闭式 <see cref="SlotSpanMaxByJDeg"/>；
    /// 长椭圆（R13）走这里。向下落到 1° 格；没有电流 ⇒ spanHi。
    /// </summary>
    public static double CutSpanMaxByJDeg(Func<double, FlangePlate> plateAt, double currentA, double spanHiDeg,
                                          double clampLenMm = 0, double jDesign = JDesignAPerMm2)
    {
        if (!(currentA > 0) || !(spanHiDeg > 0)) return spanHiDeg;
        bool Ok(double span)
        {
            var g = plateAt(span);
            return Cuts(g, currentA, clampLenMm).Where(c => c.Where.StartsWith("圆盘", StringComparison.Ordinal))
                                                 .All(c => c.JAPerMm2 <= jDesign + 1e-9);
        }
        if (Ok(spanHiDeg)) return spanHiDeg;
        double lo = 0, hi = spanHiDeg;
        if (!Ok(1.0)) return 0;
        lo = 1.0;
        for (int it = 0; it < 30 && hi - lo > 0.5; it++)
        {
            double mid = 0.5 * (lo + hi);
            if (Ok(mid)) lo = mid; else hi = mid;
        }
        return Math.Floor(lo);
    }

    /// <summary>R29：舌片在横坐标 x 处的厚度 —— 解耦时带内取臂厚、带外取杆厚；与基板同厚（NaN）时照旧。</summary>
    public static double TabThicknessAt(FlangePlate g, double x, double tTab)
        => double.IsNaN(g.TabThicknessMm) ? tTab : (g.InTabArm(x) ? g.TabArmThicknessMm : tTab);

    /// <summary>最紧的截面（J 最大）。</summary>
    public static Cut Worst(FlangePlate g, double currentA, double clampLenMm = 0)
    {
        var cuts = Cuts(g, currentA, clampLenMm);
        if (cuts.Count == 0) return new Cut("（无截面：电流为 0）", double.NaN, double.NaN);
        return cuts.OrderByDescending(c => c.JAPerMm2).First();
    }

    /// <summary>
    /// 圆盘**基板**厚下界（按 J = 10）：截面积都正比于板厚 ⇒ t_min = t_now × max_截面(J/10)。
    /// 返回值已是**基板厚**（各级倍率保持不变时，环的截面随基板同比例长）。
    /// ★ 舌片已解耦（<see cref="FlangePlate.TabThicknessMm"/> 非 NaN）时**不看舌片截面**：
    ///   舌片截面随舌片厚变、不随基板变，抬基板治不了它 —— 它由 <see cref="TongueThickMm"/> 管。
    ///   舌片与基板同厚（NaN，旧口径）时舌片截面照旧算进来。
    /// </summary>
    public static double PlateThickFloorMm(FlangePlate g, double tNowMm, double currentA, double clampLenMm = 0,
                                           double jDesign = JDesignAPerMm2)
    {
        bool decoupled = !double.IsNaN(g.TabThicknessMm);
        var cuts = Cuts(g, currentA, clampLenMm).Where(c => !(decoupled && c.OnTab)).ToList();
        if (cuts.Count == 0) return tNowMm;
        var w = cuts.OrderByDescending(c => c.JAPerMm2).First();
        if (double.IsNaN(w.JAPerMm2) || double.IsInfinity(w.JAPerMm2)) return tNowMm;
        return tNowMm * w.JAPerMm2 / jDesign;
    }

    /// <summary>
    /// 孔径上界（按设定 J）：孔心处 (宽 − 2R)·t ≥ I/J ⇒ R ≤ (宽 − I/(J t))/2。
    /// 这个式子把孔当**圆**扣弦——<paramref name="shapeRadiusRatio"/> 是该孔形状族的外接半径相对
    /// 同面积圆的放大倍数（圆角三角/方 &gt; 1，圆 = 1；由调用方按 <see cref="FlangePlate.TabHole.EqualAreaRadius"/>
    /// 算好传进来，这里不重复几何）。审查欠账（低，2026-09-09）：此前恒为圆，圆角三角/方的
    /// 真实弦长比这里算出的大 13–22 %，上界算宽了。
    /// </summary>
    public static double HoleRadiusMaxByJMm(FlangePlate g, double holeXMm, double currentA,
                                            double jDesign = JDesignAPerMm2, double shapeRadiusRatio = 1.0)
    {
        double tTab = double.IsNaN(g.TabThicknessMm) ? g.ThicknessMm : g.TabThicknessMm;
        double w = 2 * g.HalfWidth(holeXMm);
        double need = currentA / (jDesign * Math.Max(tTab, 1e-9));
        double capCircle = Math.Max(0, 0.5 * (w - need));
        double ratio = Math.Max(shapeRadiusRatio, 1e-9);
        return capCircle / ratio;
    }

    /// <summary>槽张角上界（按设定 J）：槽带各圈 (2πr − r·θ)·t ≥ I/J ⇒ θ ≤ 2π − I/(J t r)，取槽带内最紧的一圈。</summary>
    public static double SlotSpanMaxByJDeg(FlangePlate g, double rInMm, double rOutMm, double currentA,
                                           double jDesign = JDesignAPerMm2)
    {
        if (!(rOutMm > rInMm) || !(currentA > 0)) return 360.0;
        double best = 360.0;
        const int M = 40;
        for (int i = 0; i <= M; i++)
        {
            double r = rInMm + (rOutMm - rInMm) * i / M;
            double t = Math.Max(g.ThicknessAt(0, r), 1e-9);        // 盘上的点，不是舌片（同 Cuts）
            double thetaRad = 2 * Math.PI - currentA / (jDesign * t * r);
            best = Math.Min(best, Math.Max(0, thetaRad) * 180.0 / Math.PI);
        }
        return best;
    }
}
