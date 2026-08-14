using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 壳上的稳态温度场，有限体积、单元形心未知量：
///
///   ∇·(k t ∇T) + q_v·t − 2·q″(T) = 0
///
/// 与 <see cref="PlateThermal2D"/> 同一物理，区别只在**不认识网格结构** ——
/// 只用 <see cref="ShellMesh"/> 的面积、面长、形心间距与邻居，
/// 故变步长网格、开槽/阶梯任意形状、将来的三维装配都能直接用。
///
/// 单位统一到 mm 制：k [W/(mm·K)]、q″ [W/mm²]、面积 [mm²]、厚度 [mm]。
/// 表面热流两面各一份，故源项里是 **2·q″**。
/// </summary>
public sealed class ShellThermalResult
{
    public double[] T = Array.Empty<double>();
    public double TMaxC, TMinC;
    public double QGenW;          // 整片焦耳热
    public double QLossW;         // 整片表面散热
    public double QFromTubeW;     // 由管孔流入法兰的净热（>0 = 从管子抽热）
    /// <summary>
    /// 由舌片末端流进铜排的净热 W（>0 = 铜排在带走热）。**与管孔那一项同法直接算**，
    /// 不用能量恒等式反推 —— 否则「对账」就成了循环论证，验证不了任何东西。
    /// </summary>
    public double QToClampW;
    /// <summary>
    /// 能量闭合残差 W：Σ(发热−散热) + 管孔净流入 − 铜排带走。
    /// 应接近 0；显著非零说明场解没收敛或边界处理有漏。
    /// </summary>
    public double EnergyResidualW;
    public double PhiOverall;     // 自给率 = 自身发热 / 自身散热
    public double TTabEndMeanC;   // 舌片末端平均温度（铜排压接点）

    /// <summary>
    /// ★ 分区能量账（圆盘 / 舌片），按 <c>x &lt; 分界</c> 判为舌片（双舌用 |x| &gt; |分界|）。
    ///
    /// 为什么必须分区：整片只给一个「发热 &lt; 散热」的结论，指不出**哪一段**亏，
    /// 而两段的杠杆完全相反 —— 圆盘亏要缩盘径/包保温，舌片亏要窄舌加厚（J 不变、散热减半）。
    /// §4.3b 曾据闭式断言「窄舌反而更差」，那是把圆盘与舌片的散热混在一个 ΣR 里算的结果；
    /// 只有把两区分开量，才知道该动谁。全部**只统计自由单元**，口径与
    /// <see cref="EnergyResidualW"/> 一致（孔单元/舌端单元是定温边界，其收支归边界）。
    /// </summary>
    public double QGenDiscW, QLossDiscW, QGenTabW, QLossTabW;
    public double AreaDiscMm2, AreaTabMm2;
    /// <summary>面积加权平均温度 °C</summary>
    public double TDiscMeanC, TTabMeanC;
    public int Iterations;
    public double Residual;
    public bool Converged;

    /// <summary>场里有金属越过铂熔点 ⇒ **该解不存在**，不管能量账闭合得多好。</summary>
    public bool OverMelt => TMaxC > Materials.PtMeltC;
    /// <summary>场里有金属越过电阻率拟合覆盖区（1500 °C）⇒ 数值是外推的，不可引用。</summary>
    public bool OverFitRange => TMaxC > Materials.PtFitMaxC;
}

public static class ShellThermal
{
    /// <summary>
    /// 解温度场。
    /// 边界：<see cref="ShellMesh.TagHole"/> 定温 = 管根温度；
    /// <see cref="ShellMesh.TagTabEnd"/> 在 <c>BusbarClampTempC ≥ 0</c> 时定温，否则自由（自然边界）。
    /// </summary>
    /// <param name="jMagAPerMm2">各单元电流密度，来自 <see cref="ShellCurrent"/></param>
    /// <param name="tRootC">管根温度 °C（管孔处定温）</param>
    /// <param name="insulBoundaryX">保温分界 x：≥ 此值包纤维，其余裸露</param>
    /// <param name="symmetricInsul">
    /// 双舌片时置 true：改判 |x| ≤ |分界| 为保温区（两侧舌片都裸露）。
    /// ★ 为什么分区而不是全包：舌片离冷源远（约 45 mm），横向导热只有约 34 W/(m²·K)，
    ///   **表面散热是它抵抗局部热失稳的主要恢复力**；包保温会把允许电流密度
    ///   从 24.6 砍到 15.0 A/mm²（实算）。圆盘则不同 —— 它紧贴管子，
    ///   横向导热约 2400，表面项只占 1 %，包保温无害且能降低自给所需厚度。
    /// </param>
    /// <param name="tabBoundaryX">
    /// 圆盘/舌片的**几何**分界 x（用于分区能量账）。NaN = 取 <paramref name="insulBoundaryX"/>。
    /// 两者通常是同一个切点，但保温分界可以被单独挪（如「舌片也包保温」），
    /// 那时分区仍应按几何切点，否则分区账会跟着保温方案一起变，失去可比性。
    /// </param>
    /// <param name="tabInsulThickMm">
    /// ★ **舌片自己的保温厚度** mm。NaN 或 &lt;0.05 = 舌片裸露（原行为）。
    ///
    /// 为什么要把它从圆盘的 <see cref="DesignInputs.FlangeInsulThickMm"/> 里分出来：
    /// 总纲的自由度 ④ 明写「保温条件：管与法兰**分别**；哪些部位要保温、保多厚」，
    /// 而此前程序只有「以切点为界：圆盘包 / 舌片裸」这**一个二值开关**。
    /// 舌片裸露是全片最大的热漏（实测占端片散热的 90 % 以上），
    /// 它一裸就把端片推成净抽热、一全包又过冲成净倒灌 —— 中间必然存在一个零点。
    /// 给它一个连续厚度，「端片能不能自给」才从一道是非题变成一个可解的方程。
    /// </param>
    public static ShellThermalResult Solve(ShellMesh m, double[] jMagAPerMm2, DesignInputs p,
                                           double tRootC, double insulBoundaryX,
                                           bool symmetricInsul = false,
                                           int maxIter = 60000, double tol = 1e-4,
                                           double tabBoundaryX = double.NaN,
                                           double tabInsulThickMm = double.NaN)
    {
        int n = m.CellCount;
        var res = new ShellThermalResult { T = new double[n] };
        if (n == 0) return res;

        // ── 表面热流表 q″(T) [W/mm²]（原始 W/m² → ×1e-6），与 PlateThermal2D 同口径
        double charLen = 0.05;
        var bareTab = new LossTable(p.TAmbC, p.TSetC + 200, 60,
            x => Insulation.FlatOuterFlux(x, p.TAmbC, p.PtEmissivity, charLen,
                                          p.LossScale, p.FlangeAirVelocityMPerS) * 1e-6);
        var insLayers = new List<InsulationLayer>
        {
            new() { Name = "法兰保温", ThicknessMm = p.FlangeInsulThickMm,
                    K0 = p.Layer1.K0, K1 = p.Layer1.K1, Enabled = p.FlangeInsulThickMm > 1e-6 }
        };
        var insTab = new LossTable(p.TAmbC, p.TSetC + 200, 60,
            x => Insulation.PlateFlux(x, p.TAmbC, insLayers, p.OuterEmissivity, charLen,
                                      p.LossScale) * 1e-6);

        // 舌片自己的保温（见参数注释）。厚度 0 也不等于裸露 —— 裸露是铂表面 ε=0.18，
        // 而「包了 0 mm」在 PlateFlux 里走的是外覆材料 ε=0.45，两者差 2.5 倍。
        bool tabInsul = !double.IsNaN(tabInsulThickMm) && tabInsulThickMm >= 0.05;
        var tabInsLayers = new List<InsulationLayer>
        {
            new() { Name = "舌片保温", ThicknessMm = tabInsul ? tabInsulThickMm : 0,
                    K0 = p.Layer1.K0, K1 = p.Layer1.K1, Enabled = tabInsul }
        };
        var tabInsTab = tabInsul
            ? new LossTable(p.TAmbC, p.TSetC + 200, 60,
                x => Insulation.PlateFlux(x, p.TAmbC, tabInsLayers, p.OuterEmissivity, charLen,
                                          p.LossScale) * 1e-6)
            : bareTab;

        var insulated = new bool[n];
        var lossFor = new LossTable[n];
        for (int i = 0; i < n; i++)
        {
            insulated[i] = symmetricInsul
                         ? Math.Abs(m.Centroid[i].X) <= Math.Abs(insulBoundaryX)
                         : m.Centroid[i].X >= insulBoundaryX;
            lossFor[i] = insulated[i] ? insTab : tabInsTab;
        }

        // ── 舌端边界的三种模式（见 DesignInputs.BusbarConductanceWPerK 的注释）
        //   ① 热导（G ≥ 0）：q = G·(T − T_冷端)，**物理上唯一自洽的一种**，接头温度是输出
        //   ② 定温（G < 0 且 ClampTempC ≥ 0）：假设铜排能把接触点按住
        //   ③ 自由（都不给）：假设铜排完全不导热
        bool busG = p.BusbarConductanceWPerK >= 0;

        // ── 定温边界
        var isFixed = new bool[n];
        var holeCell = new bool[n];
        var tabCell = new bool[n];
        foreach (var f in m.Faces)
        {
            if (f.B >= 0) continue;
            if (f.Tag == ShellMesh.TagHole) { holeCell[f.A] = true; isFixed[f.A] = true; res.T[f.A] = tRootC; }
            else if (f.Tag == ShellMesh.TagTabEnd)
            {
                tabCell[f.A] = true;
                if (!busG && p.BusbarClampTempC >= 0) { isFixed[f.A] = true; res.T[f.A] = p.BusbarClampTempC; }
            }
        }
        for (int i = 0; i < n; i++) if (!isFixed[i]) res.T[i] = tRootC;

        // 总热导按舌端单元面积分摊
        double tabAreaTot = 0;
        for (int i = 0; i < n; i++) if (tabCell[i]) tabAreaTot += m.Area[i];
        var gBus = new double[n];
        if (busG && tabAreaTot > 1e-9)
            for (int i = 0; i < n; i++)
                if (tabCell[i]) gBus[i] = p.BusbarConductanceWPerK * m.Area[i] / tabAreaTot;

        // ── 面导度 G = k·t·L/d（k 取两侧调和平均；k 随 T 变化不大，用当前 T 更新）
        int nf = m.Faces.Count;
        var gcond = new double[nf];
        void UpdateG()
        {
            for (int k = 0; k < nf; k++)
            {
                var f = m.Faces[k];
                if (f.B < 0 || f.DistAB < 1e-12) { gcond[k] = 0; continue; }
                double kA = Materials.PtThermalK(res.T[f.A]) * 1e-3 * m.Thickness[f.A]; // W/(mm·K)·mm
                double kB = Materials.PtThermalK(res.T[f.B]) * 1e-3 * m.Thickness[f.B];
                double kf = (kA * kB) > 0 ? 2 * kA * kB / (kA + kB) : 0;
                gcond[k] = kf * f.Length / f.DistAB;
            }
        }

        var nbr = new List<(int cell, int face)>[n];
        for (int i = 0; i < n; i++) nbr[i] = new List<(int, int)>();
        for (int k = 0; k < nf; k++)
        {
            var f = m.Faces[k];
            if (f.B < 0) continue;
            nbr[f.A].Add((f.B, k)); nbr[f.B].Add((f.A, k));
        }

        // ── Picard 迭代：q″(T) 与 k(T) 用上一轮温度，欠松弛
        const double relax = 0.7;
        int it = 0; double maxd = 0;
        for (; it < maxIter; it++)
        {
            if (it % 20 == 0) UpdateG();
            maxd = 0;
            for (int i = 0; i < n; i++)
            {
                if (isFixed[i]) continue;
                double sumG = 0, sumGT = 0;
                foreach (var (c, k) in nbr[i]) { sumG += gcond[k]; sumGT += gcond[k] * res.T[c]; }
                if (sumG <= 0) continue;

                double ti = res.T[i], t = m.Thickness[i], A = m.Area[i];
                // 焦耳热 W：ρe[Ω·mm]·J²[A²/mm⁴]·t[mm]·A[mm²]
                double qv = Materials.PtResistivity(ti) * 1e3 * jMagAPerMm2[i] * jMagAPerMm2[i] * t;
                var tab = lossFor[i];
                double qs = tab.Eval(ti);                     // W/mm²，单面
                double slope = Math.Max(0, tab.Slope(ti));    // 线性化散热，稳定迭代

                // (Σg + 2·slope·A + G_铜排)·T = ΣgT + (qv − 2(qs − slope·ti))·A + G_铜排·T_冷端
                double denom = sumG + 2 * slope * A + gBus[i];
                double rhs = sumGT + (qv - 2 * (qs - slope * ti)) * A + gBus[i] * p.BusbarSinkTempC;
                double tNew = rhs / denom;
                double d = tNew - ti;
                res.T[i] = ti + relax * d;
                maxd = Math.Max(maxd, Math.Abs(d));
            }
            if (maxd < tol) { it++; break; }
        }
        res.Iterations = it; res.Residual = maxd; res.Converged = maxd < tol;

        // ── 汇总
        UpdateG();
        double gen = 0, loss = 0;
        for (int i = 0; i < n; i++)
        {
            double t = m.Thickness[i], A = m.Area[i], ti = res.T[i];
            gen += Materials.PtResistivity(ti) * 1e3 * jMagAPerMm2[i] * jMagAPerMm2[i] * t * A;
            loss += 2 * lossFor[i].Eval(ti) * A;
        }
        res.QGenW = gen; res.QLossW = loss;
        res.PhiOverall = loss > 1e-12 ? gen / loss : double.NaN;

        // 管孔净流入：对定温的孔单元，Σ 邻面导度×温差（>0 表示热从管子流进法兰）
        double q = 0;
        for (int i = 0; i < n; i++)
        {
            if (!holeCell[i]) continue;
            foreach (var (c, k) in nbr[i]) q += gcond[k] * (res.T[i] - res.T[c]);
        }
        res.QFromTubeW = q;

        // 铜排带走的热：与管孔同法，对**定温的**舌端单元累加邻面导度×温差
        // （>0 表示热从法兰流进铜排 ⇒ 取负号，因为下式算的是「流出定温单元」）
        //
        // ★ 自由端（BusbarClampTempC < 0）时这一项恒等于 0：那时舌端单元不是边界，
        //   它自己发热、自己散热。此前不分情况一律把舌端单元排除在收支之外，
        //   再把「流进它们的净热」记到「铜排带走」名下 —— 残差照样闭合（因为稳态下
        //   那个净流入正等于它们的散热减发热），但**账目是错的**：
        //   90 mm 舌片有 40 mm 压接段，自由端时那 44 % 的发热与散热被整段抹掉，
        //   还被贴上「铜排」的标签。§4.3c/§4.3d 的自由端数就是这么读出来的。
        bool clamped = !busG && p.BusbarClampTempC >= 0;
        double qc = 0;
        if (clamped)
            for (int i = 0; i < n; i++)
            {
                if (!tabCell[i]) continue;
                foreach (var (c, k) in nbr[i]) qc += gcond[k] * (res.T[c] - res.T[i]);
            }
        else if (busG)
            for (int i = 0; i < n; i++)
                if (tabCell[i]) qc += gBus[i] * (res.T[i] - p.BusbarSinkTempC);
        res.QToClampW = qc;

        // 能量闭合：自由单元的净产热 + 管孔流入 = 铜排带走
        // （定温单元自身的产热与散热由各自的边界吸收，故只累加自由单元）
        bool Excluded(int i) => holeCell[i] || (tabCell[i] && clamped);
        double genFree = 0, lossFree = 0;
        for (int i = 0; i < n; i++)
        {
            if (Excluded(i)) continue;
            double t = m.Thickness[i], A = m.Area[i], ti = res.T[i];
            genFree += Materials.PtResistivity(ti) * 1e3 * jMagAPerMm2[i] * jMagAPerMm2[i] * t * A;
            lossFree += 2 * lossFor[i].Eval(ti) * A;
        }
        res.EnergyResidualW = genFree - lossFree + res.QFromTubeW - res.QToClampW;

        // ── 分区账：圆盘 vs 舌片（口径同上，只统计自由单元）
        double xb = double.IsNaN(tabBoundaryX) ? insulBoundaryX : tabBoundaryX;
        double gD = 0, lD = 0, aD = 0, tD = 0, gT = 0, lT = 0, aT = 0, tT = 0;
        for (int i = 0; i < n; i++)
        {
            if (Excluded(i)) continue;
            double t = m.Thickness[i], A = m.Area[i], ti = res.T[i];
            double g = Materials.PtResistivity(ti) * 1e3 * jMagAPerMm2[i] * jMagAPerMm2[i] * t * A;
            double l = 2 * lossFor[i].Eval(ti) * A;
            bool onTab = symmetricInsul
                       ? Math.Abs(m.Centroid[i].X) > Math.Abs(xb)
                       : m.Centroid[i].X < xb;
            if (onTab) { gT += g; lT += l; aT += A; tT += ti * A; }
            else { gD += g; lD += l; aD += A; tD += ti * A; }
        }
        res.QGenDiscW = gD; res.QLossDiscW = lD; res.AreaDiscMm2 = aD;
        res.QGenTabW = gT; res.QLossTabW = lT; res.AreaTabMm2 = aT;
        res.TDiscMeanC = aD > 1e-9 ? tD / aD : double.NaN;
        res.TTabMeanC = aT > 1e-9 ? tT / aT : double.NaN;

        var tabT = Enumerable.Range(0, n).Where(i => tabCell[i]).Select(i => res.T[i]).ToArray();
        res.TTabEndMeanC = tabT.Length > 0 ? tabT.Average() : double.NaN;
        res.TMaxC = res.T.Max(); res.TMinC = res.T.Min();
        return res;
    }
}
