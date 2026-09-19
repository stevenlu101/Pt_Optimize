using System;
using System.Collections.Generic;

namespace PtOptimize.Core;

/// <summary>
/// 法兰平板二维稳态温度场，与 <see cref="PlateCurrent2D"/> 共用掩膜网格。
///
///   ∇·(k·t·∇T) + q_v·t − 2·q″(T, 分段) = 0        [W/mm²]
///
/// 边界条件：
///   管孔     Dirichlet T = 管根温度（法兰焊在管端，取管端金属温度）
///   自由边   自然 Neumann（边缘面积 = 周长×2mm，仅占双面面积的 3 %，忽略）
///   舌片末端 同自由边（接铜排但自由辐射，无强制冷却）
///
/// q″ 分段：X ≥ InsulBoundaryX 用保温值，否则用裸铂值。
/// 非线性（辐射 T⁴）用 Picard + 欠松弛，与一维内核同一策略。
/// </summary>
public sealed class PlateThermalResult
{
    public double[,] T = new double[0, 0];
    public double TMinC = double.MaxValue, TMaxC = double.MinValue;
    public double TMinInsulC = double.MaxValue, TMinBareC = double.MaxValue;
    public double QFromTubeW;        // 经管孔从管子抽走的净热流（正 = 抽热）
    public double QGenW, QLossW;     // 整片发热 / 散热
    public double PhiOverall;
    public double EnergyResidual;    // |gen − loss − fromTube| / gen
    public int Iterations;
    public double Converged;
    public double CellResidualAbsW, CellResidualSignedW;
    public int IsolatedCells;
    public double QFromTubeFaceSumW, FaceSumDiscrepancyW;
    /// <summary>舌片末端（铜排压接边）温度：整条边的最低/平均/最高</summary>
    public double TTabEndMinC, TTabEndMeanC, TTabEndMaxC;
}

public static class PlateThermal2D
{
    public static PlateThermalResult Solve(FlangePlate g, PlateField cur, DesignInputs p,
                                           double tRootC, int maxIter = 400000, double tol = 1e-7)
    {
        int nx = cur.Nx, nz = cur.Nz;
        double h = cur.H, t = g.ThicknessMm;
        var mask = cur.Mask;
        var res = new PlateThermalResult();

        // 分段表面热流 q″(T) [W/mm²]（原始单位 W/m² → ×1e-6）
        // ★ R48（2026-09-14，Opus 5；审查意见「圆盘保温 0 mm 时四个消费方物理含义不一致」）：两张表改调**唯一配方** DesignScreen.PlateFluxWPerM2。
        //   修的病：保温面原先无条件走 PlateFlux，厚度 0 时退到外覆材料 ε=0.45（裸铂 0.18）；另外本处原写死特征长度 0.05、保温面不传风速。
        //   默认 ConvCharLenM = 0.05、风速 0 ⇒ 包着时逐位不变。
        // R48（2026-09-15，Opus 5）：表上限与节点数同 ShellThermal（原「设定 + 200」超界静默钳住）
        var bareTab = new LossTable(p.TAmbC, ShellThermal.LossTableHiC, ShellThermal.LossTableNodes(p.TAmbC, ShellThermal.LossTableHiC),
            x => DesignScreen.PlateFluxWPerM2(p, x, 0.0) * 1e-6);
        var insTab = DesignScreen.FlangeFaceInsulated(p.FlangeInsulThickMm)
            ? new LossTable(p.TAmbC, ShellThermal.LossTableHiC, ShellThermal.LossTableNodes(p.TAmbC, ShellThermal.LossTableHiC),
                x => DesignScreen.PlateFluxWPerM2(p, x, p.FlangeInsulThickMm) * 1e-6)
            : bareTab;

        // 默认（NaN）解析为切点 = 仅圆盘保温、舌片裸露，见 FlangePlate.InsulBoundaryXMm
        double insulX = g.InsulBoundaryXResolved;
        bool Insulated(int i) => (cur.X0 + i * h) >= insulX;

        // 固定边界：管孔一圈
        var fix = new bool[nx, nz];
        var T = new double[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                if (!mask[i, j]) continue;
                T[i, j] = tRootC;
                double x = cur.X0 + i * h, z = cur.Z0 + j * h;
                if (Math.Sqrt(x * x + z * z) <= g.HoleRadiusMm + h * 1.5) fix[i, j] = true;
                // 铜排夹水冷：舌片末端整条边定温
                if (p.BusbarClampTempC >= 0 && x <= g.TabTipXMm + h * 1.5)
                { fix[i, j] = true; T[i, j] = p.BusbarClampTempC; }
            }

        double kPt = Materials.PtThermalK(p.TSetC);      // W/(mm·K) 换算见下
        double kmm = kPt * 1e-3;                          // W/(m·K) → W/(mm·K)
        double cond = kmm * t;                            // W/K，面导度系数

        int it = 0; double maxd = 0;
        for (; it < maxIter; it++)
        {
            maxd = 0;
            for (int i = 1; i < nx - 1; i++)
                for (int j = 1; j < nz - 1; j++)
                {
                    if (!mask[i, j] || fix[i, j]) continue;
                    double ts = T[i, j];
                    var tab = Insulated(i) ? insTab : bareTab;
                    double q0 = tab.Eval(ts), qp = tab.Slope(ts);

                    double sum = 0; int cnt = 0;
                    if (mask[i - 1, j]) { sum += T[i - 1, j]; cnt++; }
                    if (mask[i + 1, j]) { sum += T[i + 1, j]; cnt++; }
                    if (mask[i, j - 1]) { sum += T[i, j - 1]; cnt++; }
                    if (mask[i, j + 1]) { sum += T[i, j + 1]; cnt++; }
                    if (cnt == 0) continue;

                    // 单元能量平衡（除以 h²）：
                    //   cond·Σ(T_nb − T)/h² + q_v·t − 2·q″(T) = 0
                    double jm = cur.Jmag[i, j];                       // A/mm²
                    double rhoMm = Materials.PtResistivity(ts) * 1e3; // Ω·mm
                    double qvT = rhoMm * jm * jm * t;                 // W/mm²（已乘厚度）

                    double a = cond / (h * h) * cnt + 2 * qp;
                    double b = cond / (h * h) * sum + qvT - 2 * (q0 - qp * ts);
                    double nv = b / Math.Max(1e-12, a);
                    nv = Math.Clamp(nv, p.TAmbC, 2000);
                    double d = nv - T[i, j];
                    T[i, j] += 1.5 * d;
                    maxd = Math.Max(maxd, Math.Abs(d));
                }
            if (maxd < tol) break;
        }
        res.Iterations = it; res.Converged = maxd;

        // 汇总
        double gen = 0, loss = 0, qtube = 0;
        for (int i = 1; i < nx - 1; i++)
            for (int j = 1; j < nz - 1; j++)
            {
                if (!mask[i, j]) continue;
                double ts = T[i, j];
                if (!fix[i, j])
                {
                    res.TMinC = Math.Min(res.TMinC, ts);
                    res.TMaxC = Math.Max(res.TMaxC, ts);
                    if (Insulated(i)) res.TMinInsulC = Math.Min(res.TMinInsulC, ts);
                    else res.TMinBareC = Math.Min(res.TMinBareC, ts);

                    double jm = cur.Jmag[i, j];
                    gen += Materials.PtResistivity(ts) * 1e3 * jm * jm * t * h * h;
                    loss += 2 * (Insulated(i) ? insTab : bareTab).Eval(ts) * h * h;
                }
                else
                {
                    // 孔边界：统计从固定节点流向内部的传导热（负 = 管子被抽热）
                    if (mask[i - 1, j] && !fix[i - 1, j]) qtube += cond * (ts - T[i - 1, j]);
                    if (mask[i + 1, j] && !fix[i + 1, j]) qtube += cond * (ts - T[i + 1, j]);
                    if (mask[i, j - 1] && !fix[i, j - 1]) qtube += cond * (ts - T[i, j - 1]);
                    if (mask[i, j + 1] && !fix[i, j + 1]) qtube += cond * (ts - T[i, j + 1]);
                }
            }

        // 舌片末端整条边的温度 —— 铜排压接点，判断接头是否超温
        double teMin = double.MaxValue, teMax = double.MinValue, teSum = 0; int teN = 0;
        for (int i = 0; i < nx; i++)
        {
            double x = cur.X0 + i * h;
            if (x > g.TabTipXMm + 2 * h) continue;      // 只取末端一两列
            for (int j = 0; j < nz; j++)
            {
                if (!mask[i, j]) continue;
                double v = T[i, j];
                teMin = Math.Min(teMin, v); teMax = Math.Max(teMax, v);
                teSum += v; teN++;
            }
        }
        if (teN > 0)
        { res.TTabEndMinC = teMin; res.TTabEndMaxC = teMax; res.TTabEndMeanC = teSum / teN; }
        else
        { res.TTabEndMinC = res.TTabEndMeanC = res.TTabEndMaxC = double.NaN; }

        // 诊断：直接对每个有方程的格点求离散残差
        //   R_i = cond·Σ(T_nb − T) + (q_v·t − 2q″)·h²      [W]
        // 若 Σ|R_i| ≈ 0 而上面的 gen/loss/qtube 台账对不上，说明是台账问题而非求解问题。
        double rAbs = 0, rSigned = 0; int skipped = 0;
        for (int i = 1; i < nx - 1; i++)
            for (int j = 1; j < nz - 1; j++)
            {
                if (!mask[i, j] || fix[i, j]) continue;
                double ts = T[i, j];
                double s = 0; int c = 0;
                if (mask[i - 1, j]) { s += T[i - 1, j] - ts; c++; }
                if (mask[i + 1, j]) { s += T[i + 1, j] - ts; c++; }
                if (mask[i, j - 1]) { s += T[i, j - 1] - ts; c++; }
                if (mask[i, j + 1]) { s += T[i, j + 1] - ts; c++; }
                if (c == 0) { skipped++; continue; }          // 孤立格点：无方程
                double jm = cur.Jmag[i, j];
                double qv = Materials.PtResistivity(ts) * 1e3 * jm * jm * t;
                double ql = 2 * (Insulated(i) ? insTab : bareTab).Eval(ts);
                double R = cond * s + (qv - ql) * h * h;
                rAbs += Math.Abs(R); rSigned += R;
            }
        // 若某一区段不存在（全包覆或全裸露），其最低温应为 NaN 而不是 double.MaxValue 初值
        if (res.TMinInsulC == double.MaxValue) res.TMinInsulC = double.NaN;
        if (res.TMinBareC == double.MaxValue) res.TMinBareC = double.NaN;

        res.CellResidualAbsW = rAbs;
        res.CellResidualSignedW = rSigned;
        res.IsolatedCells = skipped;

        res.T = T;
        res.QGenW = gen; res.QLossW = loss;

        // 从管子抽走的热量由能量恒等式精确给出：
        //   Σ R_i = (gen − loss) + Q_fromTube = 0   ⇒   Q_fromTube = loss − gen
        // 上面那个逐面累加（qtube）在阶梯状圆孔边界上会漏面（实测偏小约 14 %），
        // 只保留作交叉校验，不作为输出。ΣR ≈ 0 是该恒等式成立的前提，已由诊断项确认。
        res.QFromTubeW = loss - gen;
        res.QFromTubeFaceSumW = qtube;
        res.FaceSumDiscrepancyW = qtube - res.QFromTubeW;

        res.PhiOverall = loss > 0 ? gen / loss : 0;
        // 真正的验证量：逐格残差相对于总散热
        res.EnergyResidual = loss > 0 ? Math.Abs(res.CellResidualSignedW) / loss : 1;
        return res;
    }
}
