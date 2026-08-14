using System;

namespace PtOptimize.Core;

/// <summary>
/// **铜排校核** —— 用户 2026-08-13：「铜排计算也一样」（该加重就加重，做不出来就没意义）。
///
/// 此前模型把铜排当成**完美接触的定温边界**：那条舌片末端被强制按在 300 °C，
/// 不管需要导走多少热。等于假设「你能做到 300 °C」，而不是算出「要做到需要什么」。
/// 本类把它算出来。
///
/// 铜排要同时满足四件事：
///   ① **载流**：自身截面的电流密度在自然/强制冷却下的常规范围内
///   ② **压接**：与铂舌片的接触界面电流密度 ≤ 约 1 A/mm²
///   ③ **导热**：把从铂件传来的热（本项目每片 119–269 W）带到冷端而接触端仍维持设定温度
///   ④ **接触热阻**：压紧力足够，界面温降可控
///
/// ③ 是最容易被忽略的：铜排既是导电体也是**散热器**，它得把那几百瓦带走。
/// 若带不走，接触端温度就会高于设定值，法兰随之变热 —— 整个热平衡跟着漂。
/// </summary>
public static class BusbarSizing
{
    /// <summary>铜的电阻率 Ω·m（20 °C）与温度系数</summary>
    public const double CuRho20 = 1.724e-8, CuAlpha = 3.93e-3;
    /// <summary>铜导热系数 W/(m·K)（约 300 °C）</summary>
    public const double CuK = 385.0;

    public static double CuRho(double tC) => CuRho20 * (1 + CuAlpha * (tC - 20));

    public sealed class Result
    {
        public double CurrentA, HeatW;
        /// <summary>① 载流所需截面 mm²（按给定的许用电流密度）</summary>
        public double SectionForCurrentMm2;
        /// <summary>② 压接所需接触面积 mm² 与对应的压接长度 mm</summary>
        public double ContactAreaMm2, ContactLenMm;
        /// <summary>③ 导热所需截面 mm²：把 HeatW 沿 <see cref="LengthToSinkMm"/> 传到冷端</summary>
        public double SectionForHeatMm2;
        public double LengthToSinkMm, SinkTempC, ClampTempC;
        /// <summary>三者取大 = 实际需要的截面</summary>
        public double SectionRequiredMm2 => Math.Max(SectionForCurrentMm2, SectionForHeatMm2);
        /// <summary>该截面下铜排自身的焦耳热 W（每米）</summary>
        public double CuJouleWPerM;
        public string Note = "";
    }

    /// <summary>
    /// <param name="currentA">该片承载的电流</param>
    /// <param name="heatW">要从铂件带走的热（LineRunner 的 FlangeOut.QClampW）</param>
    /// <param name="jBusAllow">铜排许用电流密度 A/mm²。自然对流 1.5–2，强制风冷 3–4</param>
    /// <param name="jContactAllow">压接界面许用电流密度 A/mm²，常规 ≤1</param>
    /// <param name="tabWidthMm">舌片宽度（= 接触宽度）</param>
    /// <param name="lengthToSinkMm">从压接点到冷端（散热器/环境）的铜排长度</param>
    /// <param name="clampTempC">压接点要维持的温度</param>
    /// <param name="sinkTempC">冷端温度</param>
    /// <param name="doubleSided">是否两面夹（接触面积翻倍）</param>
    /// </summary>
    public static Result Check(double currentA, double heatW,
                               double jBusAllow, double jContactAllow,
                               double tabWidthMm, double lengthToSinkMm,
                               double clampTempC, double sinkTempC,
                               bool doubleSided = true)
    {
        var r = new Result
        {
            CurrentA = currentA, HeatW = heatW,
            LengthToSinkMm = lengthToSinkMm, SinkTempC = sinkTempC, ClampTempC = clampTempC
        };

        // ① 载流
        r.SectionForCurrentMm2 = currentA / Math.Max(1e-9, jBusAllow);

        // ② 压接：接触面积 = 电流 / 许用界面电流密度；长度 = 面积 /(宽 × 面数)
        r.ContactAreaMm2 = currentA / Math.Max(1e-9, jContactAllow);
        r.ContactLenMm = r.ContactAreaMm2 / Math.Max(1e-9, tabWidthMm * (doubleSided ? 2 : 1));

        // ③ 导热：Q = k·A·ΔT/L  ⇒  A = Q·L/(k·ΔT)
        double dT = clampTempC - sinkTempC;
        r.SectionForHeatMm2 = dT > 1e-6
            ? heatW * (lengthToSinkMm * 1e-3) / (CuK * dT) * 1e6
            : double.PositiveInfinity;

        // 该截面下铜排自身发热（每米）—— 它也要一并带走
        double a = r.SectionRequiredMm2 * 1e-6;
        r.CuJouleWPerM = a > 1e-12 ? currentA * currentA * CuRho(clampTempC) / a : 0;

        r.Note = r.SectionForHeatMm2 > r.SectionForCurrentMm2
            ? "★ **导热**是控制项 —— 铜排截面由「把热带走」决定，不是由载流决定"
            : "载流是控制项";
        return r;
    }

    /// <summary>
    /// **风冷铜排的完整定尺寸**（用户 2026-08-14：「铜排尺寸(长宽高)与排布必须同时给出」）。
    ///
    /// <see cref="Check"/> 把铜排当「导到某个恒温冷端」，需要外部给一个 <c>lengthToSinkMm</c>——
    /// 那是个循环参数：长度是**要算的结果**，不该当输入。而且现场没有恒温冷端，
    /// 铜排是**裸露在空气里的**（§4.5：不需要水冷）。
    ///
    /// 正确模型是**散热片（fin）**：热从压接端进入，沿程由自然对流+辐射散掉。
    ///   · 稳态一维鳍片方程 θ″ = m²θ，m = √(hP/(kA))
    ///   · 足够长时 Q_base = √(h·P·k·A)·(T_base − T_amb)   ← **与长度无关**
    ///   · 有效长度取 L ≈ 3/m（此处 tanh(3)=0.995，再长几乎无增益）
    /// ⇒ 截面由「散得掉」定，长度由「散得完」定，两者一次给全。
    ///
    /// 铜排自身的焦耳热也要一并散掉，故按 Q_total = Q_铂 + I²ρ_Cu·L/A 迭代一次。
    /// </summary>
    /// <param name="widthMm">铜排宽度 = 舌片宽度（平贴压接，取等宽最自然）</param>
    /// <param name="emissivity">铜排表面发射率：氧化铜约 0.7，抛光铜仅 0.05（**差 14 倍**，务必按实际取）</param>
    public sealed class FinResult
    {
        /// <summary>压接块：宽 = 舌片宽，长 = 压接长，厚由载流定</summary>
        public double ClampWidthMm, ClampLenMm, ClampThickMm;
        /// <summary>散热段：宽/厚/长（自压接块外缘起算）</summary>
        public double FinWidthMm, FinThickMm, FinLengthMm;
        public double JBusAPerMm2, JContactAPerMm2;
        public double QFromPtW, QCuJouleW, QTotalW;
        public double HEffWPerM2K, CopperKg;
        public bool Ok;
        public string Note = "";
    }

    /// <summary>铜密度 kg/m³</summary>
    public const double CuDensity = 8960.0;

    /// <summary>
    /// ★ **为什么散热段必须比压接段宽而薄**（第一版把宽度锁死成舌片宽，
    ///   算出 30×34 mm 的方铜，10 kg/根 —— 不是解，是模型错）：
    ///
    /// 鳍片 Q ∝ √(h·P·k·A)·ΔT，宽 w ≫ 厚 t 时 P≈2w、A=wt ⇒ **Q ∝ w√t**；
    /// 有效长度 L = 3/m = 3√(kt/2h) ⇒ **L ∝ √t**；铜体积 V = w·t·L ∝ w·t^1.5。
    /// 固定 Q 消去 w：V ∝ Q·t ⇒ **体积正比于厚度 ⇒ 越薄越省铜**。
    /// 厚度的下界由载流给（w·t ≥ I/J_allow）⇒ **让载流刚好卡住**就是最省铜的解。
    ///
    /// 于是分两段给：压接块按舌片宽、厚度由载流定；散热段flare 到更宽、更薄。
    /// </summary>
    public static FinResult SizeAirCooledFin(double currentA, double qFromPtW,
                                             double tabWidthMm, double clampLenMm,
                                             double clampTempC, double tAmbC,
                                             double jBusAllow, double jContactAllow,
                                             double emissivity = 0.7,
                                             bool doubleSided = true,
                                             double hConvWPerM2K = 8.0)
    {
        var r = new FinResult
        {
            ClampWidthMm = tabWidthMm, ClampLenMm = clampLenMm, QFromPtW = qFromPtW
        };

        // 压接界面电流密度（两面夹 ⇒ 接触面积翻倍）
        double contactArea = clampLenMm * tabWidthMm * (doubleSided ? 2 : 1);
        r.JContactAPerMm2 = currentA / Math.Max(1e-9, contactArea);
        // 压接块厚度：该处宽度被舌片锁死，只能靠厚度满足载流
        r.ClampThickMm = currentA / Math.Max(1e-9, jBusAllow) / tabWidthMm;

        // 等效表面换热系数 = 自然对流 + 线性化辐射。
        // ⚠ 辐射按**沿程平均温度**线性化（不是压接端温度）：鳍片沿程降温，
        //   用根部温度会高估散热能力 ⇒ 把铜排算小，方向不安全。
        double tMean = 0.5 * (clampTempC + tAmbC) + 273.15, tAK = tAmbC + 273.15;
        double hRad = tMean > tAK + 1e-6
            ? emissivity * 5.670e-8 * (tMean * tMean + tAK * tAK) * (tMean + tAK)
            : 0;
        r.HEffWPerM2K = hConvWPerM2K + hRad;

        double dT = clampTempC - tAmbC;
        if (dT <= 1e-6) { r.Note = "压接温度不高于环境，鳍片模型不适用"; return r; }

        double k = CuK, h = r.HEffWPerM2K, jA = jBusAllow * 1e6;   // A/m²
        // 载流卡死：t = I/(jA·w)。代入 Q ∝ w√t 解 w：
        //   √(2·h·k·w²·t)·ΔT ≥ Q，t = I/(jA w) ⇒ √(2hk·w·I/jA)·ΔT ≥ Q
        //   ⇒ w ≥ (Q/(ΔT))²·jA/(2hk·I)
        double qNeed = qFromPtW, w = 0.02, t = 0.005;
        for (int it = 0; it < 60; it++)
        {
            double wNew = qNeed * qNeed / (dT * dT) * jA / Math.Max(1e-9, 2 * h * k * currentA);
            wNew = Math.Max(wNew, tabWidthMm * 1e-3);              // 不窄于舌片
            double tNew = currentA / (jA * wNew);
            double L = 3.0 * Math.Sqrt(k * tNew / (2 * h));
            double qCu = currentA * currentA * CuRho(0.5 * (clampTempC + tAmbC)) * L / (wNew * tNew);
            double qN2 = qFromPtW + 0.5 * qCu;                     // 自身焦耳热约一半从这端散
            bool done = Math.Abs(wNew - w) < 1e-5 && Math.Abs(qN2 - qNeed) < 0.5;
            w = wNew; t = tNew; qNeed = qN2;
            if (done) break;
        }

        r.FinWidthMm = w * 1e3;
        r.FinThickMm = t * 1e3;
        r.FinLengthMm = 3.0 * Math.Sqrt(k * t / (2 * h)) * 1e3;
        r.JBusAPerMm2 = currentA / Math.Max(1e-9, r.FinWidthMm * r.FinThickMm);
        r.QCuJouleW = currentA * currentA * CuRho(0.5 * (clampTempC + tAmbC))
                      * (r.FinLengthMm * 1e-3) / (w * t);
        r.QTotalW = qFromPtW + 0.5 * r.QCuJouleW;
        r.CopperKg = CuDensity * (w * t * (r.FinLengthMm * 1e-3)
                     + tabWidthMm * 1e-3 * clampLenMm * 1e-3 * r.ClampThickMm * 1e-3
                       * (doubleSided ? 2 : 1));

        r.Ok = r.JContactAPerMm2 <= jContactAllow + 1e-9;
        r.Note = r.JContactAPerMm2 > jContactAllow
            ? $"★ 压接界面 J={r.JContactAPerMm2:0.00} 超 {jContactAllow:0.0} ⇒ 压接段加长或改多点压接"
            : "载流/压接/散热三条都过";
        return r;
    }
}
