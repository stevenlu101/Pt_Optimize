using System;

namespace PtOptimize.Core;

/// <summary>
/// 法兰平板的二维电流场。
///
///   ∇·(σ∇V) = 0      （等厚平板，σ 取工作温度下的常数）
///   J = −σ∇V
///
/// 边界条件：
///   舌片末端整条边  Dirichlet V = 1   （整条边压接铜排，铜电导率为铂的 6 倍，视为等电位）
///   管孔边界        Dirichlet V = 0   （电流全部交给管壁）
///   其余自由边      自然 Neumann      （掩膜网格上「邻居在域外则该面通量为零」自动满足）
///
/// 离散采用有限体积五点格式，故**电流严格守恒**：
/// 流入舌片末端的电流必等于流出管孔的电流，这一点由 <see cref="PlateField.ConservationError"/> 检验。
/// </summary>
public sealed class PlateField
{
    public bool[,] Mask = new bool[0, 0];
    public double[,] V = new double[0, 0];        // 归一化电位
    public double[,] Jmag = new double[0, 0];     // A/mm²
    public double[,] Sheet = new double[0, 0];    // 面电流 K = J·t [A/mm]
    public double X0, Z0, H;                      // 网格原点 mm / 步长 mm
    public int Nx, Nz;

    public double CurrentInA, CurrentOutA;
    public double ConservationError;              // |in−out| / in
    public double JMaxAPerMm2, JMeanAPerMm2;
    public double JMaxXMm, JMaxZMm, JMaxRMm;   // J_max 位置
    public double TotalGenW;                      // 整片法兰的焦耳发热 W
    public int Iterations;
    public double Residual;
}

/// <summary>法兰平面轮廓（由 Pt_Heater.3dm 提取）</summary>
public sealed class FlangePlate
{
    public double DiscRadiusMm = 60.0;      // Ø120
    public double HoleRadiusMm = 26.0;      // Ø52（= 管外径）
    public double TabEndXMm = -200.0;
    public double TabEndHalfWidthMm = 40.0; // 末端宽 80
    public double ThicknessMm = 2.0;
    /// <summary>
    /// 保温分界：X ≥ 此值的区域包纤维，其余裸露。
    ///
    /// **默认 NaN = 自动取切点，即「仅圆盘保温、舌片全裸」** —— 这是现场实况
    /// （2026-08-10 用户确认：纤维只包铂管与法兰圆，法兰圆以外均不保温）。
    /// 显式赋 −200 表示全包（含舌片），是 `--insul` 扫描与若干旧算例用的假设。
    ///
    /// 本类不参与 JSON 序列化（算例文件只序列化 <see cref="DesignInputs"/>），
    /// 故此处用 NaN 作哨兵是安全的；<see cref="DesignInputs.FlangeDrawOverrideW"/>
    /// 那边则不能，原因见其注释。
    /// </summary>
    public double InsulBoundaryXMm = double.NaN;

    /// <summary>
    /// **双舌片对称进电**（两片舌相隔 180°）。
    ///
    /// 单舌片的代价在本项目里逐条暴露出来：
    ///   · 舌片是全片最窄的电流通道 —— 实测 J = 32.1 A/mm²，比孔周还紧
    ///   · 压接界面电流密度 9 A/mm²（3 mm 压接），远超压接接头的 ≤1 常规
    ///   · 铜排被散热需求定到 40×21.8 mm
    ///   · 电流从单侧进来绕过管孔，在靠舌片那侧堆成峰值（J_max/J_rms = 2.17）
    ///
    /// 双舌片把每舌电流减半 ⇒ 舌片 J 减半、每舌铜排热减半、压接长可减半，
    /// 且电流场对称。代价是多一片舌的铂重。
    ///
    /// 求解器无需改动：电流场的边界是「舌端 V=1、管孔 V=0」再按总电流定标，
    /// 两个舌端同为 V=1 时会**自然对称分流**。
    /// </summary>
    public bool TwoTabs = false;

    /// <summary>
    /// **等宽（矩形）舌片**：自与圆盘的交界起就保持 <see cref="TabEndHalfWidthMm"/> 不变，
    /// 不再从切点的半宽（≈盘半径）线性收到末端。
    ///
    /// 为什么要它：梯形舌片在**两条约束上同时吃亏**——
    ///   · 导热漏 Q = k·A̅·ΔT/ℓ 用的是**平均**截面（梯形的平均截面大）
    ///   · 局部失稳 J = I/A_min 用的是**最窄**截面（梯形的末端截面小）
    ///   实测这一项就吃掉约 1.25 倍裕度，再加上收口处的角点电流集中约 1.39 倍，
    ///   两者相乘 1.7 倍 —— 而闭式给的总裕度只有约 2 倍（§4.3e）。
    ///   等宽舌片把这两项一起去掉，且根部不再有那一大片又宽又不发热的料。
    ///
    /// 交界位置：x = −√(R² − w²)（半宽 w 的直边与圆的交点），w ≥ R 时退化为 −0。
    /// </summary>
    public bool TabParallel = false;

    /// <summary>
    /// **舌片自己的保温厚度** mm。NaN = 舌片裸露（现场实况，也是默认）。
    ///
    /// 与 <see cref="InsulBoundaryXMm"/> 的关系：那个只决定「哪一段算圆盘、哪一段算舌片」，
    /// 本字段决定舌片那一段**包多厚**。此前两者是同一个二值开关（要么裸、要么按圆盘的
    /// <see cref="DesignInputs.FlangeInsulThickMm"/> 全包），中间没有档位 ——
    /// 而端片的热平衡零点恰好落在中间（裸露时净抽热、全包时净倒灌）。
    /// 详见 <see cref="ShellThermal"/>.Solve 的同名参数。
    /// </summary>
    public double TabInsulThickMm = double.NaN;

    /// <summary>解析后的保温分界：NaN ⇒ 切点（仅圆盘保温）</summary>
    public double InsulBoundaryXResolved
        => double.IsNaN(InsulBoundaryXMm) ? Tangent().X : InsulBoundaryXMm;

    /// <summary>孔周局部加厚：半径 ≤ ThickenRadiusMm 的区域厚度取 ThickenedMm</summary>
    /// <summary>末端延长段：自 TabEndXMm 再伸 ExtensionMm，半宽由 40 线性张开到 ExtHalfWidthMm</summary>
    public double ExtensionMm = 0.0;
    public double ExtHalfWidthMm = 40.0;
    public double TabTipXMm => TabEndXMm - ExtensionMm;

    public double ThickenRadiusMm = 0.0;
    public double ThickenedMm = 2.0;

    /// <summary>
    /// 舌片厚度 mm。NaN = 与圆盘同厚（默认，即原来的单一厚度行为）。
    ///
    /// 分开设厚是一个独立自由度：单位面积发热 ∝ J²·t = (K/t)²·t = K²/t，
    /// 故**舌片加厚同时降低 J 与单位面积发热** ⇒ 舌片变凉 ⇒ 辐射损失塌下来（∝T⁴）。
    /// 而舌片裸露、是当前最大热漏。典型用法：薄圆盘（把热发在管根附近）+ 厚舌片。
    /// 分界取圆盘与舌片的切点，与保温分界 <see cref="InsulBoundaryXResolved"/> 一致。
    /// </summary>
    public double TabThicknessMm = double.NaN;

    /// <summary>
    /// 圆盘的**阶梯**厚度分区（用户 2026-08-10：径向连续渐变加工难，改用阶梯）。
    ///
    /// <see cref="DiscStepRadiiMm"/> 为各级**外**半径（自小到大），
    /// <see cref="DiscStepThicknessMm"/> 为对应厚度，长度须一致。
    /// r ≤ 第 k 级半径的第一个 k 生效；都不命中则取 <see cref="ThicknessMm"/>（外缘厚度）。
    /// 空数组 = 圆盘等厚（默认）。
    ///
    /// 例：半径 {35, 48}、厚度 {1.0, 1.5}，外缘 2.0
    ///   ⇒ r≤35 取 1.0；35&lt;r≤48 取 1.5；r&gt;48 取 2.0。
    ///
    /// 机理：单位面积发热 ∝ K²/t，孔边那一级减薄 ⇒ 该处发热骤增，
    /// 而那里正是管根冷点所在 —— 等于把热直接补在缺口上。
    /// 代价：孔周 J 本就是全片峰值（HANDOVER §4.6），减薄会让它更高。
    /// 与 <see cref="TabThicknessMm"/>（加厚舌片压 J）方向相反、可同时用。
    ///
    /// 与既有的 <see cref="ThickenRadiusMm"/>/<see cref="ThickenedMm"/> 是同一类东西
    /// （那是单级、且用于**加**厚）；本字段非空时优先级更高，两者不要混用。
    /// </summary>
    public double[] DiscStepRadiiMm = Array.Empty<double>();

    /// <summary>与 <see cref="DiscStepRadiiMm"/> 一一对应的厚度 mm</summary>
    public double[] DiscStepThicknessMm = Array.Empty<double>();

    /// <summary>该点的板厚 mm。优先级：圆盘阶梯 > 孔周加厚 > 舌片厚 > 圆盘外缘厚。</summary>
    public double ThicknessAt(double x, double z)
    {
        // 舌片先判：阶梯是按半径分的，只对圆盘有意义
        bool onTab = !double.IsNaN(TabThicknessMm) &&
                     (TwoTabs ? Math.Abs(x) > Math.Abs(Tangent().X) : x < Tangent().X);
        if (onTab) return TabThicknessMm;

        double r = Math.Sqrt(x * x + z * z);
        int nStep = Math.Min(DiscStepRadiiMm.Length, DiscStepThicknessMm.Length);
        for (int k = 0; k < nStep; k++)
            if (r <= DiscStepRadiiMm[k]) return DiscStepThicknessMm[k];
        if (nStep > 0) return ThicknessMm;                       // 阶梯已给，外缘取基准厚

        if (ThickenRadiusMm > HoleRadiusMm && r <= ThickenRadiusMm) return ThickenedMm;
        return ThicknessMm;
    }

    /// <summary>切点：舌片直边与 Ø120 圆相切处（等宽舌片时是直边与圆的**交点**）</summary>
    public (double X, double HalfW) Tangent()
    {
        if (TabParallel)
        {
            double w = Math.Min(TabEndHalfWidthMm, DiscRadiusMm);
            return (-Math.Sqrt(Math.Max(0, DiscRadiusMm * DiscRadiusMm - w * w)), w);
        }
        // T = R(cosθ, sinθ) 在圆上，切点条件 (P − T)·T = 0：
        //   px·R·cosθ − R²cos²θ + pz·R·sinθ − R²sin²θ = 0
        //   ⇒ px·cosθ + pz·sinθ = R
        // 写成 A·cos(θ − φ) = R，A = |P|，φ = atan2(pz, px)，取上切点分支 θ = φ − acos(R/A)
        double px = TabEndXMm, pz = TabEndHalfWidthMm, R = DiscRadiusMm;
        double amp = Math.Sqrt(px * px + pz * pz);
        double phi = Math.Atan2(pz, px);
        double th = phi - Math.Acos(Math.Clamp(R / amp, -1, 1));
        return (R * Math.Cos(th), R * Math.Sin(th));
    }

    public double HalfWidth(double x)
    {
        // 双舌片：整形关于 z 轴对称 ⇒ 用 −|x| 代入单舌片的公式
        if (TwoTabs) return HalfWidthSingle(-Math.Abs(x));
        return HalfWidthSingle(x);
    }

    private double HalfWidthSingle(double x)
    {
        var (xt, wt) = Tangent();
        if (x > DiscRadiusMm || x < TabTipXMm) return 0;
        if (x < TabEndXMm)                      // 延长段：线性张开
        {
            double ue = (TabEndXMm - x) / Math.Max(1e-9, ExtensionMm);
            return TabEndHalfWidthMm + (ExtHalfWidthMm - TabEndHalfWidthMm) * ue;
        }
        if (x >= xt) return Math.Sqrt(Math.Max(0, DiscRadiusMm * DiscRadiusMm - x * x));
        if (TabParallel) return TabEndHalfWidthMm;      // 等宽：交界之后不再收口
        double u = (x - xt) / (TabEndXMm - xt);
        return wt + (TabEndHalfWidthMm - wt) * u;
    }

    public bool Inside(double x, double z)
        => Math.Abs(z) <= HalfWidth(x) && x * x + z * z >= HoleRadiusMm * HoleRadiusMm;
}

public static class PlateCurrent2D
{
    /// <param name="h">网格步长 mm</param>
    /// <param name="tempField">可选温度场（与网格同形）。给出时按 σ(T)=1/ρe(T) 逐点取值；
    /// 为空则退回全场常数 σ。铂在 700–1300 °C 间 ρe 变化 48 %，忽略它会把电流分布算偏。</param>
    public static PlateField Solve(FlangePlate g, double totalCurrentA,
                                   double rhoOhmM, double h = 0.5,
                                   int maxIter = 20000, double tol = 1e-10,
                                   double[,]? tempField = null, double tRefC = 1300,
                                   double[,]? thickField = null)
    {
        double x0 = g.TabTipXMm - h, x1 = g.DiscRadiusMm + h;
        double z1 = Math.Max(g.DiscRadiusMm, g.ExtHalfWidthMm) + h;
        int nx = (int)Math.Round((x1 - x0) / h) + 1;
        int nz = (int)Math.Round((2 * z1) / h) + 1;

        var f = new PlateField { X0 = x0, Z0 = -z1, H = h, Nx = nx, Nz = nz };
        var mask = new bool[nx, nz];
        var fixedV = new bool[nx, nz];
        var V = new double[nx, nz];

        for (int i = 0; i < nx; i++)
        {
            double x = x0 + i * h;
            for (int j = 0; j < nz; j++)
            {
                double z = -z1 + j * h;
                mask[i, j] = g.Inside(x, z);
                if (!mask[i, j]) continue;

                // 舌片末端整条边：等电位 V = 1
                if (x <= g.TabTipXMm + h * 1.5) { fixedV[i, j] = true; V[i, j] = 1.0; }
                // 管孔边界：V = 0（一圈厚度 1.5h 的环带）
                double r = Math.Sqrt(x * x + z * z);
                if (r <= g.HoleRadiusMm + h * 1.5) { fixedV[i, j] = true; V[i, j] = 0.0; }
            }
        }

        // 局部板厚：给定厚度场时逐点取，否则用几何规则
        double ThickAt(int i, int j)
            => thickField != null ? thickField[i, j]
               : g.ThicknessAt(x0 + i * h, -z1 + j * h);

        // 局部电导率 σ(T) = 1/ρe(T)，归一化到参考温度使无温度场时退化为原行为
        double sigRef = 1.0 / Materials.PtResistivity(tRefC);
        double SigmaAt(int i, int j)
            => tempField == null ? 1.0
               : (1.0 / Materials.PtResistivity(tempField[i, j])) / sigRef;

        // SOR 迭代（掩膜外邻居不参与 → 自然 Neumann）
        double omega = 2.0 / (1.0 + Math.PI / Math.Max(nx, nz));
        int it = 0; double res = 0;
        for (; it < maxIter; it++)
        {
            res = 0;
            for (int i = 1; i < nx - 1; i++)
                for (int j = 1; j < nz - 1; j++)
                {
                    if (!mask[i, j] || fixedV[i, j]) continue;
                    // 面导度 ∝ 面处板厚（变厚度时不能再用等权平均）
                    double xc = x0 + i * h, zc = -z1 + j * h, tc = ThickAt(i, j);
                    double sc = SigmaAt(i, j);
                    double sum = 0, wsum = 0;
                    void Acc(int a2, int b2, double xf, double zf)
                    {
                        if (!mask[a2, b2]) return;
                        // 面导度 ∝ t·σ，两侧取算术平均
                        double tf2 = 0.5 * (tc + ThickAt(a2, b2));
                        double sf = 0.5 * (sc + SigmaAt(a2, b2));
                        double wf = tf2 * sf;
                        sum += wf * V[a2, b2]; wsum += wf;
                    }
                    Acc(i - 1, j, xc - h, zc); Acc(i + 1, j, xc + h, zc);
                    Acc(i, j - 1, xc, zc - h); Acc(i, j + 1, xc, zc + h);
                    if (wsum <= 0) continue;
                    double nv = sum / wsum;
                    double d = nv - V[i, j];
                    V[i, j] += omega * d;
                    res = Math.Max(res, Math.Abs(d));
                }
            if (res < tol) break;
        }
        f.Iterations = it; f.Residual = res;

        // 电流：J = −σ∇V。先按 σ=1、V 无量纲算「形状电流」，再整体定标到 totalCurrentA
        double sigmaShape = 1.0;
        double ShapeCurrentAcross(int i)      // 穿过 x = x0+i·h 这一列的通量
        {
            double s = 0;
            for (int j = 0; j < nz; j++)
                if (mask[i, j] && mask[i + 1, j])
                {
                    double tf2 = 0.5 * (ThickAt(i, j) + ThickAt(i + 1, j));
                    double sf2 = 0.5 * (SigmaAt(i, j) + SigmaAt(i + 1, j));
                    s += -(V[i + 1, j] - V[i, j]) / h * h * (tf2 / g.ThicknessMm) * sf2;
                }
            return s * sigmaShape;
        }

        // 在舌片直边段取一列作为流入基准（避开端部与孔）
        int iRef = (int)Math.Round((g.TabTipXMm + 30 - x0) / h);
        double shapeI = Math.Abs(ShapeCurrentAcross(iRef));
        double scale = shapeI > 1e-12 ? totalCurrentA / (shapeI * g.ThicknessMm) : 0;

        // 守恒检查：末端流入 vs 孔周流出
        int iIn = (int)Math.Round((g.TabTipXMm + 5 - x0) / h);
        f.CurrentInA = Math.Abs(ShapeCurrentAcross(iIn)) * g.ThicknessMm * scale;
        double outSum = 0;
        for (int i = 1; i < nx - 1; i++)
            for (int j = 1; j < nz - 1; j++)
            {
                if (!mask[i, j] || !fixedV[i, j]) continue;
                double r = Math.Sqrt(Math.Pow(x0 + i * h, 2) + Math.Pow(-z1 + j * h, 2));
                if (r > g.HoleRadiusMm + h * 1.5) continue;   // 只统计孔边界
                double xh = x0 + i * h, zh = -z1 + j * h, th0 = ThickAt(i, j);
                void AccOut(int a2, int b2, double xf, double zf)
                {
                    if (!mask[a2, b2] || fixedV[a2, b2]) return;
                    double tf2 = 0.5 * (th0 + ThickAt(a2, b2));
                    double sf3 = 0.5 * (SigmaAt(i, j) + SigmaAt(a2, b2));
                    outSum += (V[a2, b2] - V[i, j]) / h * h * (tf2 / g.ThicknessMm) * sf3;
                }
                AccOut(i - 1, j, xh - h, zh); AccOut(i + 1, j, xh + h, zh);
                AccOut(i, j - 1, xh, zh - h); AccOut(i, j + 1, xh, zh + h);
            }
        f.CurrentOutA = Math.Abs(outSum) * g.ThicknessMm * scale;
        f.ConservationError = f.CurrentInA > 0
            ? Math.Abs(f.CurrentInA - f.CurrentOutA) / f.CurrentInA : 1;

        // |J| 场（中心差分），单位 A/mm²
        var J = new double[nx, nz];
        var K = new double[nx, nz];
        double jmax = 0, jsum = 0; int jn = 0;
        double gen = 0;
        double rhoOhmMm = rhoOhmM * 1e3;             // Ω·m → Ω·mm
        for (int i = 1; i < nx - 1; i++)
            for (int j = 1; j < nz - 1; j++)
            {
                if (!mask[i, j]) continue;
                double dvx = Grad(V, mask, i, j, 1, 0, h);
                double dvz = Grad(V, mask, i, j, 0, 1, h);
                // 深度平均下守恒量是面电流 K = J·t，方程 ∇·(σt∇V)=0 给出 K = −σt∇V，
                // 故 J = K/t = −σ∇V —— 与局部厚度无关，不得再乘厚度因子。
                double tLoc = ThickAt(i, j);
                double jm = Math.Sqrt(dvx * dvx + dvz * dvz) * scale * SigmaAt(i, j);  // A/mm²
                J[i, j] = jm;
                K[i, j] = jm * tLoc;
                if (jm > jmax)
                {
                    jmax = jm;
                    f.JMaxXMm = x0 + i * h; f.JMaxZMm = -z1 + j * h;
                    f.JMaxRMm = Math.Sqrt(f.JMaxXMm * f.JMaxXMm + f.JMaxZMm * f.JMaxZMm);
                }
                jsum += jm; jn++;
                // q_v = ρ·J²  [W/mm³]，体元 = h·h·t
                gen += rhoOhmMm * jm * jm * h * h * tLoc;
            }

        f.Mask = mask; f.V = V; f.Jmag = J; f.Sheet = K;
        f.JMaxAPerMm2 = jmax;
        f.JMeanAPerMm2 = jn > 0 ? jsum / jn : 0;
        f.TotalGenW = gen;
        return f;
    }

    private static double Grad(double[,] V, bool[,] m, int i, int j, int di, int dj, double h)
    {
        bool a = m[i - di, j - dj], b = m[i + di, j + dj];
        if (a && b) return (V[i + di, j + dj] - V[i - di, j - dj]) / (2 * h);
        if (b) return (V[i + di, j + dj] - V[i, j]) / h;
        if (a) return (V[i, j] - V[i - di, j - dj]) / h;
        return 0;
    }
}
