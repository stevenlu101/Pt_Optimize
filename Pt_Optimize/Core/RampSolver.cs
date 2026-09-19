using System;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 空管升温核算 —— 规程一：**首先要能确保升温过程达到设定目标温度（此时管内无玻璃）**。
///
/// 与稳态解的根本区别：**玻璃项整个不存在**。
/// 稳态解里 <c>SegmentSolver</c> 的源项含 −hg·πD·(T−Tg)、稳定判据的 β 含 hg·πD；
/// 空管时这两项都为零，热稳定极限 I_stab = √(βA/(dρe/dT)) 随之收紧。
///
/// 本模块给出模型此前**完全没有的壁厚下界**：
///
///     P_max = J_allow² · A · ρe · L    ⇒    P_max ∝ A ∝ 壁厚
///
/// 也就是说，减薄壁厚的同时也在削减可用功率。稳态解只把 J 当**上界**
/// （越薄 J 越大，顶到许用值就停），方向相反的下界一条都没有 ——
/// 而真实尺寸正是由升温工况定的（额定功率约为稳态的 6 倍）。
///
/// 集总模型：整段视为一个温度节点。铂壁薄、导热高、段长仅 300 mm，
/// 轴向温差在升温期远小于与环境的温差，集总足够定功率。
/// （冷点分布要看稳态解，不在本模块职责内。）
/// </summary>
public sealed class RampResult
{
    public bool Reached;                 // 是否在限时内达到目标温度
    public double HoursToTarget;         // 达到目标所需小时数（未达成则为 NaN）
    public double TPeakC;                // 限时内达到的最高温度
    public double CurrentA, JAPerMm2;
    public double TubeAreaMm2;
    public double PowerAtTargetW;        // 目标温度处的电功率
    public double LossAtTargetW;         // 目标温度处的总散热
    public double IStabA;                // 空管热稳定极限
    /// <summary>电流被热稳定极限压在了许用电流密度之下（厚壁时会发生，不是故障）</summary>
    public bool StabilityLimited;
    public double CapMetalJPerK, CapInsulJPerK;   // 热容分解，便于判断谁主导
    public string Note = "";

    /// <summary>
    /// ★ R48（2026-09-15，Opus 5；常驻数值把关人第十四轮「其余散热表超界检测」）：本次管散热表覆盖的温度区间 °C
    /// （下限 = 环境温度；上限 = 目标、参考温度、起点三者之最 + 200 K，见 <see cref="RampSolver.Solve"/> 里建表处的注释）。
    /// </summary>
    public double LossTableLoC = double.NaN, LossTableHiC = double.NaN;
    /// <summary>
    /// 本次积分**实际查表用到的温度**里有超出表区间、而且钳住的方向会让结果偏乐观或说不清的 ⇒ <see cref="Reached"/>／<see cref="HoursToTarget"/> 不可引用。
    /// 查表用到的温度 = 起始温度、积分达到的最高温 <see cref="TPeakC"/>、参考温度（法兰散热按它缩放）、目标温度（热稳定极限的斜率）。
    /// ★ 2026-09-15 Opus 5（审查意见 major：环境温度 35 °C 时起点 25 °C 被误判超界）：积分走过的温度（起始温度、积分最高温）**只查上限**，
    ///   低于表下限（= 环境温度）不算超界 —— 推导见 <see cref="RampSolver.Solve"/> 里那段注释，不依赖任何实测数据；
    ///   参考温度与目标温度两头都查（它们进的是比值与斜率，钳住的方向说不清）。
    /// ★ 2026-09-15 Opus 5（审查意见 minor）：表上限改为三者之最 + 200 K 之后，上限那头按构造只剩「积分最高温冲过 最高已知温度 + 200 K」才会超
    ///   （积分一步 2 s，要升温速率超过 100 K/s 才冲得过去，检测照留）；能触发的主要是下限那头：参考温度（整线里 = 本段控温点）或目标温度低于环境温度，以及 NaN。
    /// 用它的判据（LineRunner 的集总升温用时参考量）超界即判不了。
    /// </summary>
    public bool LossTableExceeded;
    /// <summary>
    /// ★ 2026-09-15 Opus 5（审查意见 minor：原先只有一句带内部参数名「参考温度」的文字，整线附注照抄进界面）：超界的查表温度，结构化 —— 哪一个、多少 °C。
    /// 调用方按自己的叫法生成说明文字（整线里参考温度就是本段控温点），不从 <see cref="LossTableNote"/> 里抠字。空 = 没超界。
    /// </summary>
    public (RampTableProbe Probe, double TempC)[] LossTableOut = Array.Empty<(RampTableProbe, double)>();
    /// <summary>超界时的一句通用说明（空 = 没超界）；给没有自己叫法的调用方用。</summary>
    public string LossTableNote = "";
}

/// <summary>R48（2026-09-15，Opus 5）：集总升温积分里查管散热表用到的四个温度（<see cref="RampResult.LossTableOut"/> 标明是哪一个超界）。</summary>
public enum RampTableProbe
{
    /// <summary>升温起点（<c>fromC</c>）。</summary>
    Start,
    /// <summary>积分达到的最高温（<see cref="RampResult.TPeakC"/>）。</summary>
    Peak,
    /// <summary>参考工况温度（<c>tRefC</c>，法兰散热按它缩放；整线里 = 本段控温点）。</summary>
    Reference,
    /// <summary>升温目标（<c>targetC</c>，热稳定极限取这里的斜率）。</summary>
    Target,
}

public static class RampSolver
{
    /// <summary>
    /// 空管升温积分。
    /// </summary>
    /// <param name="wallMm">管壁厚 mm</param>
    /// <param name="massFlangePairG">本段两端法兰合计质量 g（随管一起被加热）</param>
    /// <param name="flangeGenRefW">参考工况下**单片法兰自身的焦耳发热** W（稳态耦合解的 QGenW）</param>
    /// <param name="flangeCurrentRefA">上述发热对应的电流 A —— 用来反推法兰电阻 R_f = QGen/I²</param>
    /// <param name="tRefC">上述两值对应的参考温度 °C</param>
    /// <param name="fromC">起始温度 °C</param>
    /// <param name="targetC">目标温度 °C</param>
    /// <param name="maxHours">限时 h</param>
    public static RampResult Solve(DesignInputs p, double wallMm,
                                   double massFlangePairG, double flangeGenRefW,
                                   double flangeCurrentRefA, double tRefC,
                                   double fromC, double targetC, double maxHours)
    {
        var res = new RampResult();

        double ri = p.TubeIdMm * 0.5e-3, w = wallMm * 1e-3, rOut = ri + w;
        double area = Math.PI * (rOut * rOut - ri * ri);      // m²
        double L = p.TubeLength;
        res.TubeAreaMm2 = area * 1e6;

        // ── 散热：只有保温层向环境（无玻璃）。法兰按同一温度形状缩放。
        //    发射率要按「有没有保温」取：裸管辐射的是铂表面，包了保温才是保温外表面。
        bool insulated = false;
        foreach (var lay in p.Layers) if (lay.Enabled && lay.ThicknessMm > 1e-6) insulated = true;
        double eps = insulated ? p.OuterEmissivity : p.PtEmissivity;

        // CylinderLoss 内部要迭代求外表面温度，积分里逐步调用太慢（下面二分会调数十万次），
        // 与 SegmentSolver 一样先打成样条表，顺带拿到解析斜率供 I_stab 用。
        // ★ R48（2026-09-15，Opus 5；审查意见 minor「升温目标比控温点低 200 K 以上是界面可填的合法输入，原上限 目标 + 200 K 让参考温度出表，这条参考量白丢」）：
        //   上限由「目标 + 200 K」改为「查表会用到的已知温度（目标、参考温度、起点）里最高的 + 200 K」。
        //   200 K 是原式给目标留的余量，原样沿用到三者之最（规则由查表用到哪些温度推出，不看任何实测数据）。
        //   数值变化范围（如实写）：参考温度与起点都不高于目标 ⇒ 三者之最 = 目标 ⇒ 表逐位不变（生产整线：控温点 ≤ 升温目标 1150 °C、起点 25 °C 就是这种）；
        //   参考温度或起点高于目标 ⇒ 表变宽、60 个样条节点的间距变大 ⇒ 数会变：原先超出 目标 + 200 K 的部分是被钳住算错的，现在按真值查；
        //   在 目标～目标 + 200 K 之间的，只有插值级的差。NaN 不参与取最大（比较为假），照旧由下面的超界检测报出来。
        double tHiKnown = targetC;
        if (tRefC > tHiKnown) tHiKnown = tRefC;
        if (fromC > tHiKnown) tHiKnown = fromC;
        var lossTab = new LossTable(p.TAmbC, tHiKnown + 200, 60,
            tC => Insulation.CylinderLoss(tC, p.TAmbC, rOut, p.Layers, eps,
                                          p.Posture == Orientation.Vertical, L,
                                          p.LossScale).QPerLength);
        double LossPerM(double tC) => lossTab.Eval(tC);

        double lossRef = Math.Max(1e-9, LossPerM(tRefC));

        // ── 法兰：**既散热也发热**，两者都要算。
        //    只算散热（早先的做法）会把升温门槛抬得过高：在 Φ≈1 的优化点上法兰本就热自给，
        //    对管子近乎中性；而升温电流大于稳态电流，发热 ∝I² 涨得比散热快，
        //    Φ_升温>1 时法兰实际是在**帮着**加热。
        //    法兰电阻由参考工况反推：R_f = QGen_ref / I_ref²，随温度按 ρe(T) 缩放。
        double rFlangeRef = flangeGenRefW > 0 && flangeCurrentRefA > 1e-9
            ? flangeGenRefW / (flangeCurrentRefA * flangeCurrentRefA) : 0;   // Ω @ tRefC
        double rhoRefT = Math.Max(1e-30, Materials.PtResistivity(tRefC));
        // Φ≈1 ⇒ 参考工况下单片法兰的散热 ≈ 其自身发热
        double flangeLossRefW = flangeGenRefW;

        double NetFlangePairW(double tC, double iA)
        {
            if (rFlangeRef <= 0) return 0;
            double gen = iA * iA * rFlangeRef * (Materials.PtResistivity(tC) / rhoRefT);
            double loss = flangeLossRefW * (LossPerM(tC) / lossRef);
            return 2 * (loss - gen);          // 两片；>0 表示净耗，<0 表示净帮忙
        }

        // ── 热容
        double massTube = Materials.PtDensity * area * L;      // kg
        double massFlange = massFlangePairG * 1e-3;            // kg
        double CapMetal(double tC) => (massTube + massFlange) * Materials.PtCp(tC);

        // 保温层热容：各层体积×密度×比热。乘 0.5 是梯度因子 ——
        // 保温层内表面跟着金属走、外表面接近环境，平均温升约为内表面的一半。
        double capInsul = 0;
        double r = rOut;
        foreach (var lay in p.Layers)
        {
            if (!lay.Enabled || lay.ThicknessMm <= 1e-6) continue;
            double rNext = r + lay.ThicknessMm * 1e-3;
            double vol = Math.PI * (rNext * rNext - r * r) * L;
            capInsul += vol * lay.DensityKgM3 * lay.CpJKgK * 0.5;
            r = rNext;
        }
        res.CapInsulJPerK = capInsul;
        res.CapMetalJPerK = CapMetal(0.5 * (fromC + targetC));

        // ── 空管热稳定极限：β 不含玻璃项（这正是与稳态解的分野）
        //    稳态解里 β = lossTab.Slope + hg·π·D，空管时后一项为零，极限随之收紧。
        double betaEmpty = lossTab.Slope(targetC);            // W/(m·K)
        double drhoDt = Materials.RhoRef * (Materials.AlphaFit + 2 * Materials.BetaFit * targetC);
        res.IStabA = Math.Sqrt(Math.Max(1e-9, betaEmpty * area / Math.Max(1e-30, drhoDt)));

        // ── 电流：J_allow 是**上限而非必须值**。厚壁时 I=J_allow·A 会越过热稳定极限
        //    （I_stab ∝ √A 而 I ∝ A，故 I/I_stab ∝ √A 随壁厚增长），
        //    此时只是「不能用满许用电流」，不是不可行 —— 取二者较小并留 10 % 裕度。
        // ★ 升温电流的上限用**管子**的许用 J（用户给的现场数），不是法兰那个占位值。
        //   原来用 p.JAllow=10 ⇒ 管壁 0.6 的升温上限 954 A < 稳态所需 1045 A ⇒ 判成不可行。
        double iAllow = p.TubeJAllow * area;
        double iCap = 0.9 * res.IStabA;
        double current = Math.Min(iAllow, iCap);
        res.StabilityLimited = iCap < iAllow;
        res.CurrentA = current;
        res.JAPerMm2 = current / area * 1e-6;

        // ── 积分 C(T)·dT/dt = P_elec(T) − P_loss(T)
        double t = fromC, time = 0, dt = 2.0;                  // s
        double maxSec = maxHours * 3600.0;
        double tPeak = fromC;

        while (time < maxSec)
        {
            double R = Materials.PtResistivity(t) * L / area;   // Ω
            double pElec = current * current * R;
            double net = pElec - LossPerM(t) * L - NetFlangePairW(t, current);
            double cap = CapMetal(t) + capInsul;

            if (net <= 0)
            {
                // 电功率已被散热吃平 —— 再等也升不上去，这就是该壁厚的温度天花板
                res.Note = $"在 {t:0.0} °C 处电功率与散热持平，无法继续升温";
                break;
            }

            t += net / cap * dt;
            time += dt;
            if (t > tPeak) tPeak = t;
            if (t >= targetC)
            {
                res.Reached = true;
                res.HoursToTarget = time / 3600.0;
                break;
            }
        }

        res.TPeakC = tPeak;
        if (!res.Reached) res.HoursToTarget = double.NaN;

        // ★ R48（2026-09-15，Opus 5）：解完核一下查表用到的温度在不在表里（LossTable.Eval／Slope 超界静默钳住）。
        //   四个温度就是上面真正喂给 LossPerM／lossTab.Slope 的那几个：起点、积分最高温、参考温度、目标温度。
        // ★ 2026-09-15 Opus 5（审查意见 major）：**积分走过的温度只查上限**。原先四个温度两头都查 ⇒ 界面「环境温度」填 35 °C 时，
        //   写死的起点 25 °C 低于表下限（= 环境温度）⇒ 每一段都判超界 ⇒ 每个设计的集总升温用时都「无法判定」，是误报。
        //   推导（不依赖任何实测数据，只用模型本身的性质）：
        //     ① 表下限 = 环境温度 T_amb（上面 new LossTable(p.TAmbC, …)）；
        //     ② 表面散热 q(T) 对温度单调不减（温差越大散热越多，CylinderLoss 的对流、辐射、导热各项都是），故 T < T_amb 时 q(T) ≤ q(T_amb)；
        //     ③ 积分里散热只以两种形式进净功率：管 −LossPerM(t)·L、法兰 −flangeLossRefW·LossPerM(t)/lossRef（lossRef 与 t 无关、为正）——
        //        钳住 ⇒ 用 q(T_amb) 代替 q(t) ⇒ 两项都只会**多扣**，净功率只会偏小；发热项 I²R(t) 与热容不查表，不受影响；
        //     ④ 净功率偏小 ⇒ 升温更慢 ⇒ 算出的用时只会偏长、「限时内到得了」只会被误判成到不了，不会反过来。
        //   ⇒ 低于表下限的钳住是**偏保守**的，不会把不能用的说成能用，不需要判不了；超上限则相反（少扣散热、偏乐观），照判。
        //   参考温度（进比值 LossPerM(t)/lossRef 的分母）与目标温度（进热稳定极限的斜率）钳住后方向说不清，仍两头都查。
        res.LossTableLoC = lossTab.LoC; res.LossTableHiC = lossTab.HiC;
        // 2026-09-15 Opus 5：超界的温度记成结构化列表（哪一个、多少度），说明文字由调用方按自己的叫法生成；这里只留一句通用的
        var outOfTable = new System.Collections.Generic.List<(RampTableProbe Probe, double TempC)>();
        foreach (var (what, tq) in new[] { (RampTableProbe.Start, fromC), (RampTableProbe.Peak, tPeak) })
            if (!(tq <= lossTab.HiC)) outOfTable.Add((what, tq));      // NaN 也算超界
        foreach (var (what, tq) in new[] { (RampTableProbe.Reference, tRefC), (RampTableProbe.Target, targetC) })
            if (!lossTab.Covers(tq)) outOfTable.Add((what, tq));
        if (outOfTable.Count > 0)
        {
            res.LossTableExceeded = true;
            res.LossTableOut = outOfTable.ToArray();
            string Name(RampTableProbe w) => w switch
            {
                RampTableProbe.Start => "起始温度", RampTableProbe.Peak => "积分最高温", RampTableProbe.Reference => "参考工况温度", _ => "目标温度",
            };
            res.LossTableNote = $"{string.Join("、", outOfTable.Select(o => $"{Name(o.Probe)} {o.TempC:0.#} °C"))} 不在管表面散热表 {lossTab.LoC:0.#}～{lossTab.HiC:0.#} °C 内，"
                              + "表外那部分散热只能按表端点的值算";
        }

        double rT = Materials.PtResistivity(targetC) * L / area;
        res.PowerAtTargetW = current * current * rT;
        res.LossAtTargetW = LossPerM(targetC) * L + NetFlangePairW(targetC, current);
        if (res.Note.Length == 0 && !res.Reached)
            res.Note = $"限时 {maxHours:0.#} h 内只升到 {tPeak:0.0} °C";
        return res;
    }

    /// <summary>
    /// 反问：满足升温要求所需的**最小壁厚**。
    /// 单调性：壁厚 ↑ ⇒ A ↑ ⇒ 可用功率 P=J²·A·ρ·L ↑（线性），而热容也 ↑（线性），
    /// 但散热与壁厚无关 ⇒ 净升温能力随壁厚单调增。故可二分。
    /// </summary>
    public static double MinWallForRampMm(DesignInputs p, double massFlangePairG,
                                          double flangeGenRefW, double flangeCurrentRefA, double tRefC,
                                          double fromC, double targetC, double maxHours,
                                          double loMm = 0.10, double hiMm = 6.0)
    {
        // 电流已按 min(J_allow·A, 0.9·I_stab) 取，故「能否升到」随壁厚单调 ——
        // 薄壁受限于可用功率不足，加厚只会改善，不再有上界。
        bool Ok(double wmm)
            => Solve(p, wmm, massFlangePairG, flangeGenRefW, flangeCurrentRefA, tRefC,
                     fromC, targetC, maxHours).Reached;

        if (!Ok(hiMm)) return double.NaN;      // 最厚也不行
        if (Ok(loMm)) return loMm;             // 最薄就行，下界不由升温决定
        for (int k = 0; k < 60 && hiMm - loMm > 1e-4; k++)
        {
            double mid = 0.5 * (loMm + hiMm);
            if (Ok(mid)) hiMm = mid; else loMm = mid;
        }
        return 0.5 * (loMm + hiMm);
    }
}
