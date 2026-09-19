using System;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **设计电流：20 °C/h 升温全程所需管电流的峰值**（2026-09-08，用户给的设计因果链第 ① 步）。
///
/// 用户原话：「在 20 °C/h 下所须通过铂金管的电流（功率）是多少 → 再去决定法兰大小与尺寸与舌片横截面积」。
/// 稳态有玻璃时后两段的玻璃在加热管子、电流很小；**空管升温才是尺寸的依据**
/// （<see cref="RampSolver"/> 档头早就写着「真实尺寸正是由升温工况定的，额定功率约为稳态的 6 倍」，只是没接进尺寸链）。
/// 用户 2026-09-08 定：升温目标**全线 1150 °C**（= <c>LineCase.RampTargetC</c>）。
///
/// 模型：**管子自己的准静态电流**（<see cref="RampTwoNode.QuasiStaticCurrentA"/>）：
///   I²·R_管(T) = Q_管散热(T) + C_管·dT/dt
/// 沿升温全程 25→1150 °C 取最大（散热随温度涨，峰在目标温度处）。20 K/h 下热容项约 1 W、散热千瓦级。
///
/// ⚠ 为什么**不**用两节点模型（管 + 法兰）的电流当尺寸依据（2026-09-08 实测改的）：
///   两节点里法兰自热（共用片 √3 倍电流）会让法兰比管热、热倒灌回管子 ⇒ 管电流算少；
///   而法兰电阻随板厚变、第 1 轮闭式估计（0.6 mm 薄板）与第 2 轮场解相差 20 倍 ⇒ 设计电流 1160 → 1485 A 跳。
///   尺寸依据不许被「法兰帮管子发热」省掉、也不许随法兰自己漂 ⇒ 取管子单独所需的电流（确定、闭式）。
///   ⚠ 2026-09-15 Opus 5（I 路，合并把关待办 P1-15）：原句写「上界、确定、闭式」，「上界」不成立 —— 它**不是整线稳态段电流的上界**。同一次整线判定里实测（B2 设计，
///     deliverable\R48_设计电流与段电流_同一次整线_本次开跑于2026-09-15_191729.txt；设计电流取自同一次 Judge 写进 FlangeOut.DesignCurrentA 的值，复算逐位相同）：
///     空管到温稳态 段0 电流 1077.0307 A 对设计电流 1072.0221 A，大 5.009 A（+0.467 %）；共用片 HC1|HC2 接头 1858.5403 对 1856.7968 A（+0.094 %）；
///     带玻璃稳态 段0 1072.8174 A 也大 0.795 A（+0.074 %）。原因（推理，未拆）：管子单独的准静态电流不含法兰抽热与段间耦合，整线段解两者都有。
///     截面 J 与升温判据仍按设计电流算（口径没改）；这里只改说法。
///   两节点的结果另算一份印出来当对照（<see cref="Result.TwoNodeSegPeakA"/>），不进尺寸链。
/// ⚠ R48 G2 复审（2026-09-15 Opus 5）：这份对照仍是**旧的两节点模型**（没有经舌片流进铜排的通道、舌片按裸铂、参考电阻配管根温度）——
///   求解器定下角时（<see cref="ForLine"/>）还没有场；整线判据表调用时有场（参考电阻取场），但每段两片各跑一次，没有接节点标定 —— 接不接、怎么接待定。
///   整线判据表「升温期法兰−管峰值」那一行已改为逐项标定。界面字串（<see cref="Result.Describe"/>）写明「旧模型」。
///
/// 片的接头电流按 <see cref="LineSolver.JointCurrentA"/> 矢量合成（共用片 120° 相位差 ⇒ 等电流时 √3 倍）。
/// </summary>
public static class DesignCurrent
{
    public sealed class Result
    {
        /// <summary>每段升温全程的峰值电流 A。</summary>
        public double[] SegPeakA = Array.Empty<double>();
        /// <summary>每片接头电流 A（共用片矢量合成）。</summary>
        public double[] PlateA = Array.Empty<double>();
        /// <summary>该段电流曾被二次侧上限（管 J 许用 × 管截面）截住 ⇒ 跟不上设定速率。</summary>
        public bool[] SegClipped = Array.Empty<bool>();
        /// <summary>★ R20（用户 2026-09-08）：升温所需电流**未截住时**的全程峰值折成管 J（A/mm²）—— 判据 ① 的实测值。</summary>
        public double RampTubeJPeakAPerMm2 = double.NaN;
        /// <summary>管 J 许用（判据 ① 的限值；截住与否就看它）。</summary>
        public double TubeJAllowAPerMm2 = double.NaN;
        /// <summary>有段被截住 = 按设定速率升不到目标。</summary>
        public bool Clipped => SegClipped.Any(c => c);
        /// <summary>对照：两节点模型（管 + 相邻较重那片法兰）的全程峰值 —— 含法兰自热倒灌，不进尺寸链。</summary>
        public double[] TwoNodeSegPeakA = Array.Empty<double>();
        public double RampRateKPerH, FromC, TargetC;
        public string Describe() =>
            $"设计电流（温控 {RampRateKPerH:0} °C/h 空管升温 {FromC:0}→{TargetC:0} °C，管子准静态 I²R=散热+C·Ṫ 全程峰值）：段 "
          + string.Join("/", SegPeakA.Select(a => a.ToString("0"))) + " A ⇒ 片 "
          + string.Join("/", PlateA.Select(a => a.ToString("0"))) + " A（共用片矢量合成）"
          // ★ R48 G2 复审（2026-09-15 Opus 5；审查意见 minor「对照电流仍是旧模型却没有说明」）：两节点对照没有接节点标定，仍是旧模型 —— 界面上写明，免得与升温那一行（已补通道、逐项标定）混为一谈
          // R48 G2 复审二（2026-09-15 Opus 5；审查意见 minor「同一个已判不可引用的旧两节点模型照样给出对照电流」）：数仍印（逐片圆盘保温下游门靠它咬住圆盘保温），标明不可引用
          + $"　对照·两节点旧模型（含法兰自热；没有经舌片流进铜排的通道、舌片按裸铂；不可引用）{string.Join("/", TwoNodeSegPeakA.Select(a => a.ToString("0")))} A（不进尺寸链）"
          + (SegClipped.Any(c => c) ? "　⚠ 有段被管 J 许用上限截住 ⇒ 跟不上设定速率" : "");
    }

    /// <summary>从设计记录算（求解器用：定下角时还没有场）。</summary>
    public static Result ForLine(DesignSpec d, DesignInputs p, LineResult? last = null)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));
        if (p is null) throw new ArgumentNullException(nameof(p));
        var lc = d.BuildCase(p, checkRamp: false);
        double floor = d.DiscFloorMm(p);
        var plates = Enumerable.Range(0, d.FlangeCount).Select(j => d.Plate(j, floor)).ToArray();
        (double, double)? Ref(int j) =>
            last is { Ok: true } && j < last.Flanges.Length
            && last.Flanges[j].QGenW > 0 && last.Flanges[j].CurrentA > 0
                ? (last.Flanges[j].QGenW / (last.Flanges[j].CurrentA * last.Flanges[j].CurrentA), last.Flanges[j].TRootC)
                : null;
        // ★ 用 lc.Base 不用 p：BuildCase 把设计记录的**管保温层**（TubeInsulMm）与法兰保温写进的是 lc.Base，
        //   原始 p 带的是开箱默认。实测（2026-09-08 杠杆扫描）传 p 时管保温 5→80 mm 设计电流纹丝不动 —— 哑的。
        return Compute(plates, d.WallMm, lc.Base, lc.RampFromC, lc.RampTargetC, lc.RampRateKPerH,
                       lc.SegmentCount, lc.SetpointC, Ref, lc.DiscInsulEffectiveAt);   // R48（2026-09-14，Opus 5）：逐片圆盘保温同源
    }

    /// <summary>
    /// 核心：给定各片几何算每段峰值电流与每片接头电流。
    /// <paramref name="refFromField"/>：第 j 片若有场解，回 (法兰参考电阻 Ω, 参考温度 °C)；没有回 null 走闭式估计。
    /// <paramref name="discInsulMmAt"/>：★ R48（2026-09-14，Opus 5）第 j 片圆盘保温 mm（传 LineCase.DiscInsulEffectiveAt）。
    ///   只进两节点**对照**（不进尺寸链），但它印在「法兰截面 J」的 Note 里，原先读整线 p.FlangeInsulThickMm ⇒ 带逐片圆盘保温的设计那句对照是按整线值算的。
    ///   2026-09-14 Opus 5 改为**必填**（审查意见）：原默认 null 时退回板件口径，而图纸路径的判据几何板件不带逐片值 ⇒ 新调用方忘了传就静默按整线算。
    ///   没有 LineCase 的调用方显式传 <c>j =&gt; plates[j].DiscInsulEffectiveMm(p)</c>。
    /// </summary>
    public static Result Compute(FlangePlate[] plates, double wallMm, DesignInputs p,
                                 double fromC, double targetC, double rateKPerH,
                                 int segCount, double[] setpointC,
                                 Func<int, (double rRef, double tRef)?> refFromField,
                                 Func<int, double> discInsulMmAt)
    {
        if (plates is null || plates.Length == 0) throw new ArgumentException("没有法兰几何", nameof(plates));
        if (discInsulMmAt is null) throw new ArgumentNullException(nameof(discInsulMmAt), "逐片圆盘保温取值必须给（LineCase.DiscInsulEffectiveAt），不许静默退回整线值");
        int n = segCount;
        var res = new Result
        {
            SegPeakA = new double[n], SegClipped = new bool[n], PlateA = new double[n + 1],
            TwoNodeSegPeakA = new double[n],
            RampRateKPerH = rateKPerH, FromC = fromC, TargetC = targetC,
        };
        double holeR = plates[0].HoleRadiusMm;
        double tubeAreaMm2 = Math.PI * (holeR * holeR - (holeR - wallMm) * (holeR - wallMm));
        double iCap = p.TubeJAllowAPerMm2 * tubeAreaMm2;          // 二次侧上限：管 J 许用 × 管截面
        FlangePlate P(int j) => plates[Math.Min(j, plates.Length - 1)];

        // ── 尺寸依据：管子准静态电流沿全程取最大（每段同一根管 ⇒ 各段相同；仍逐段存，段参数将来可能不同）
        double iQsPeak = 0, iRawPeak = 0; bool qsClipped = false;
        const int NT = 60;
        for (int k = 0; k <= NT; k++)
        {
            double tC = fromC + (targetC - fromC) * k / NT;
            double iq = RampTwoNode.QuasiStaticCurrentA(p, wallMm, tC, rateKPerH);
            iRawPeak = Math.Max(iRawPeak, iq);                      // 未截住的所需电流（判据 ① 看它）
            if (iq > iCap) { iq = iCap; qsClipped = true; }
            iQsPeak = Math.Max(iQsPeak, iq);
        }
        res.RampTubeJPeakAPerMm2 = tubeAreaMm2 > 1e-12 ? iRawPeak / tubeAreaMm2 : double.NaN;
        res.TubeJAllowAPerMm2 = p.TubeJAllowAPerMm2;

        for (int i = 0; i < n; i++)
        {
            res.SegPeakA[i] = iQsPeak;
            res.SegClipped[i] = qsClipped;
            // ── 对照用的两节点模型（含法兰自热倒灌）：两端各配一片各跑一次取大者；**不进尺寸链**
            double peak = 0; bool clipped = false;
            foreach (int j in new[] { i, i + 1 })
            {
                var g = P(j);
                double vol = CoupledSolver.PlateVolumeMm3(g);
                double area = CoupledSolver.PlateArea(g, 20001);
                // ★ R48 续（2026-09-14，Opus 5）：保温面积按板件的唯一判定（FlangePlate.UnderDiscInsulation）划 ——
                //   默认分界按半径（圆盘整块包法兰保温），显式分界仍按 x。本块是对照模型、不进尺寸链，
                //   改它不动设计电流；改是为了对照值与主解（ShellThermal）同一个保温口径。
                double aIns = double.IsNaN(g.InsulBoundaryXMm) ? AreaWithinDisc(g) : AreaFromX(g, g.InsulBoundaryXResolved);
                double aBare = Math.Max(0, area - aIns);
                bool shared = j > 0 && j < n;

                var rf = refFromField(j);
                double tRef = rf?.tRef ?? setpointC[Math.Min(i, setpointC.Length - 1)];
                double rRef = rf?.rRef ?? ClosedFormResistanceOhm(g, tRef);

                var inp = new RampTwoNode.Inputs
                {
                    WallMm = wallMm,
                    FlangeMassG = vol * Materials.PtDensity * 1e-6,
                    FlangeAreaInsulMm2 = aIns, FlangeAreaBareMm2 = aBare,
                    FlangeResistanceRefOhm = rRef, FlangeRefTempC = tRef,
                    HoleRadiusMm = holeR,
                    PlateEqOuterRadiusMm = Math.Sqrt(area / Math.PI + holeR * holeR),
                    FlangeThickMm = vol / Math.Max(1e-9, area),
                    DesignCurrentA = 0.5 * p.TubeJAllowAPerMm2 * tubeAreaMm2,   // 只作其它模式的参考，温控模式不用它
                    MaxCurrentA = p.TubeJAllowAPerMm2 * tubeAreaMm2,
                    FromC = fromC, TargetC = targetC,
                    RampRateKPerH = rateKPerH,
                    MaxHours = (targetC - fromC) / Math.Max(0.1, rateKPerH) * 1.4,
                    SharedFactor = shared ? Math.Sqrt(3.0) : 1.0,
                    Mode = RampControl.TemperatureRamp,
                };
                // ★ R48（2026-09-14，Opus 5）：RampTwoNode 读 p.FlangeInsulThickMm ⇒ 按本片圆盘保温克隆一份（值与整线相同就不克隆，旧口径逐位不变）
                double insJ = discInsulMmAt(j);
                DesignInputs pj = p;
                if (!insJ.Equals(p.FlangeInsulThickMm)) { pj = SegmentSolver.Clone(p); pj.FlangeInsulThickMm = insJ; }
                var rt = RampTwoNode.Solve(pj, inp);
                if (rt.PeakCurrentA > peak) { peak = rt.PeakCurrentA; clipped = rt.CurrentClipped; }
            }
            res.TwoNodeSegPeakA[i] = peak;          // 只当对照印出来
        }
        for (int j = 0; j <= n; j++) res.PlateA[j] = LineSolver.JointCurrentA(res.SegPeakA, j);
        return res;
    }

    /// <summary>
    /// R48 续（2026-09-14，Opus 5）：**圆盘整块**（r ≤ 盘半径）的板面积 mm²（扣管孔）——
    /// 保温默认按半径划时，法兰保温包的就是这一块。与 <see cref="AreaFromX"/> 同一积分法：
    /// 逐 x 取「板外形半宽」与「盘圆半宽」的较小者，扣管孔。舌片伸出盘圆的部分不计。
    /// </summary>
    public static double AreaWithinDisc(FlangePlate g, int n = 20001)
    {
        double r = g.DiscRadiusMm;
        if (!(r > 0)) return 0;
        double x0 = Math.Max(g.TabTipXMm, -r), x1 = r;
        if (!(x1 > x0)) return 0;
        double dx = (x1 - x0) / (n - 1), a = 0;
        for (int i = 0; i < n; i++)
        {
            double x = x0 + i * dx, w = dx * (i == 0 || i == n - 1 ? 0.5 : 1.0);
            double circ = Math.Sqrt(Math.Max(0, r * r - x * x));
            double hole = Math.Abs(x) <= g.HoleRadiusMm ? Math.Sqrt(g.HoleRadiusMm * g.HoleRadiusMm - x * x) : 0;
            a += 2 * Math.Max(0, Math.Min(g.HalfWidth(x), circ) - hole) * w;
        }
        return a;
    }

    /// <summary>x ≥ xFrom 那部分的板面积 mm²（扣管孔），与 <see cref="CoupledSolver.PlateArea"/> 同一积分法。</summary>
    public static double AreaFromX(FlangePlate g, double xFrom, int n = 20001)
    {
        double x0 = Math.Max(g.TabTipXMm, xFrom), x1 = g.DiscRadiusMm;
        if (!(x1 > x0)) return 0;
        double dx = (x1 - x0) / (n - 1), a = 0;
        for (int i = 0; i < n; i++)
        {
            double x = x0 + i * dx, w = dx * (i == 0 || i == n - 1 ? 0.5 : 1.0);
            double hole = Math.Abs(x) <= g.HoleRadiusMm ? Math.Sqrt(g.HoleRadiusMm * g.HoleRadiusMm - x * x) : 0;
            a += 2 * Math.Max(0, g.HalfWidth(x) - hole) * w;
        }
        return a;
    }

    /// <summary>
    /// 法兰参考电阻的闭式估计 Ω：舌片 ρ·L/(w·t) + 圆盘径向扩散 ρ·ln(R/r)/(2π t)。
    /// 只在还没有场解时用；它只影响两节点模型里法兰节点的温度，不影响管电流的量级。
    /// </summary>
    public static double ClosedFormResistanceOhm(FlangePlate g, double tC)
    {
        double rho = Materials.PtResistivity(tC);                     // Ω·m
        double tTab = (double.IsNaN(g.TabThicknessMm) ? g.ThicknessMm : g.TabThicknessMm) * 1e-3;
        var (xT, hwT) = g.Tangent();
        double lTab = Math.Max(1e-3, (xT - g.TabTipXMm) * 1e-3);
        double wTab = Math.Max(1e-3, (hwT + g.TabEndHalfWidthMm) * 1e-3);   // 梯形取平均宽
        double rTab = rho * lTab / (wTab * Math.Max(tTab, 1e-6));
        double tDisc = Math.Max(g.ThicknessMm, 1e-6) * 1e-3;
        double rDisc = rho * Math.Log(Math.Max(1.001, g.DiscRadiusMm / Math.Max(1e-6, g.HoleRadiusMm))) / (2 * Math.PI * tDisc);
        return rTab + rDisc;
    }
}
