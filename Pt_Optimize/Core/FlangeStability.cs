using System;
using System.Collections.Generic;

namespace PtOptimize.Core;

/// <summary>
/// **法兰的热稳定判据** —— 用户 2026-08-13 澄清的失效机理：
///
///   保温过头 → 温度↑ → 铂电阻↑ → 同样电流下发热 P=I²R ↑ → 温度更↑ → … → 烧断
///
/// 这是**正反馈失控**，与净热流方向**无关**。即使法兰此刻在从管子吸热
/// （早先判为「安全方向」），只要发热随温度上升得比散热快，它照样会失控。
///
/// ★ 早先的判据用错了：拿 Φ（自身发热/自身散热）与抽热方向去判，那是**平衡**判据；
///   而用户描述的是**稳定性**判据。两者互不蕴含 ——
///   一个系统可以处在平衡态却是不稳定平衡（铅笔立在尖上）。
///
/// 判据：
///
///   dQ_散热/dT  &gt;  dP_发热/dT = P · (1/ρe)(dρe/dT)
///
/// 铂的电阻温度系数在 1150 °C 约 5.48e-4 /K，故每升 1 K，发热增加约 0.055 %。
/// 散热侧则有三条通道，各自的 dQ/dT 相加：
///   ① 表面辐射+对流：包保温后大幅削弱 ← **保温过头就是削这一项**
///   ② 沿舌片传到铜排夹：与夹持温度无关，只看导度，是**稳定器**
///   ③ 经管孔传给管子：同上
///
/// 管侧早有同类判据（<see cref="RampSolver"/> 的 I_stab = √(βA/(dρe/dT))），
/// 法兰侧此前完全没有 —— 本类补上。
/// </summary>
public static class FlangeStability
{
    public sealed class Result
    {
        /// <summary>自身发热 W 与其温度导数 W/K</summary>
        public double QGenW, DGenDT;
        /// <summary>三条散热通道的导数 W/K</summary>
        public double DSurfDT, DClampDT, DTubeDT;
        public double DLossDT => DSurfDT + DClampDT + DTubeDT;
        /// <summary>稳定裕度 = dQ_散热/dT ÷ dP_发热/dT。**必须 &gt; 1**；&lt; 1 即热失控</summary>
        public double Margin => DGenDT > 1e-12 ? DLossDT / DGenDT : double.PositiveInfinity;
        public bool Stable => !double.IsNaN(Margin) && Margin > 1.0;
        /// <summary>
        /// 评估温度超出物性拟合区间 ⇒ 判不了（而不是「稳定」）。
        /// ★ R48 G2（2026-09-15 Opus 5）：调用方给了场标定的夹持导度、而它标定不出来（NaN）⇒ 同样判不了 —— 不退回几何算法各算各的。
        /// </summary>
        public bool Undetermined => double.IsNaN(DGenDT) || double.IsNaN(DClampDT);
        /// <summary>
        /// ★ R48 G2 复审二（2026-09-15 Opus 5；审查意见 minor「字符串协议」）：夹持那一项导度是不是调用方给的**稳态场标定值**（true）——
        /// 否则按舌片几何算（false）。判断一律读本位；原先 LineRunner 拿界面文字 <see cref="ClampSource"/> 与「稳态场标定」比，文字一改就静默失效。
        /// </summary>
        public bool ClampFromField;
        /// <summary>★ R48 G2（2026-09-15 Opus 5）：夹持那一项导度的出处，**只供界面文字**，由 <see cref="ClampFromField"/> 映射（R48 G2 复审二 2026-09-15 Opus 5 改为派生，不再单独赋值）。</summary>
        public string ClampSource => ClampFromField ? "稳态场标定" : "舌片几何";
        /// <summary>
        /// ★ R48 G2 复审（2026-09-15 Opus 5；审查意见 minor「割线当切线用」）：夹持那一项**按舌片几何算**的导度 W/K（改动前的算法，调用方给不给场标定值都算一份），
        /// 与只把夹持项换成它的裕度 —— 灵敏度对照：场标定值是割线（带走热 ÷ 温差，含舌片自身焦耳热流进铜排的那一份），当温升导数用可能偏大；
        /// 几何导度是纯导热的切线值，但按舌端半宽 × 厚、从切点量到压接段内边，读的是整线设定的边界模式。两者之间就是这一项的不确定范围。
        /// 求和顺序与 <see cref="DLossDT"/> 相同 ⇒ 调用方不给场标定值时 <see cref="MarginGeomClamp"/> 与 <see cref="Margin"/> 逐位相同。
        /// </summary>
        public double DClampGeomDT = double.NaN;
        public double MarginGeomClamp => DGenDT > 1e-12 ? (DSurfDT + DClampGeomDT + DTubeDT) / DGenDT : double.PositiveInfinity;
        /// <summary>失控前还能承受多少温升 K（线性外推，仅供量级参考）</summary>
        public double HeadroomK;
        public string Note = "";
    }

    /// <summary>
    /// 判一片法兰。<paramref name="qGenW"/> 与 <paramref name="tPlateC"/> 取自壳解，
    /// 其余由几何与工况给出。
    /// </summary>
    /// <param name="areaInsulMm2">包保温的单面面积；<paramref name="areaBareMm2"/> 为裸露部分</param>
    /// <param name="tabSectionMm2">舌片导热截面（= 2×半宽×厚），沿舌长传向铜排夹</param>
    /// <param name="tabLenMm">舌片长度</param>
    /// <param name="holeSectionMm2">管孔处的导热截面（= 2π·孔半径×厚）</param>
    /// <param name="tabInsulThickMm">
    /// 舌片自己的保温厚度 mm。NaN = 舌片裸露（现场实况，也是默认）。
    ///
    /// ★ 2026-08-23 补：原来只有一个 <paramref name="insulThickMm"/>，
    ///   于是整片只能「全包」或「全裸」。而设计记录构型是**分区**的
    ///   —— 圆盘包 <c>FlangeInsulThickMm</c>、舌片包 <c>TabInsulMm[j]</c>，
    ///   两者常常差一个量级。用单一厚度去算 dQ_散热/dT，
    ///   在「盘包厚、舌近裸」的真实构型上会把散热侧算**偏小**（偏危险侧不是偏安全侧）。
    ///   本参数让 <paramref name="areaBareMm2"/> 那一片按自己的保温算。
    ///   传 NaN 即退回原行为（裸），故老调用点不受影响。
    /// </param>
    /// <param name="clampConductanceWPerK">
    /// ★ R48 G2（2026-09-15 Opus 5；物理把关人第十一轮「两处用同一个来源」）：夹持那一项的导度 W/K 由调用方给。
    /// 整线（LineRunner.FlangeLumped）传升温两节点同一份**稳态场标定**的导度，并按同一个夹持假设取 G·(1 − r)（RampTwoNode.NodeCalibration.StabClampWPerK；
    /// R48 G2 复审 2026-09-15 Opus 5：原传 G，与升温那一行「夹持随法兰按比例升」的假设不一致）；
    /// 标定不出来传 NaN ⇒ 本条判不了（<see cref="Result.Undetermined"/>）。
    /// null = 老算法（舌片几何导度 k·截面/舌长，热导边界再与铜排串联）：整线与命令行两处整片热稳定仪器都不再走它（R48 G2 复审：原注释说命令行「拿不到稳态场」不属实，
    /// 那两处都先跑了整线解，现改传 LineRunner.FlangeLumped 的同一份标定）；老算法的值仍总算一份放进 <see cref="Result.DClampGeomDT"/> 当灵敏度对照。
    /// 为什么整线不再用几何导度：它按舌端半宽 × 厚、从切点量到压接段的长度算，不含舌片自身发热沿舌长流进铜排的份额，
    /// 与场里实际带走的热（整面接触 159～274 W）对不上；而且读的是整线 DesignInputs 的夹持温度／铜排热导，不是本片的。
    /// </param>
    public static Result Check(DesignInputs p, double qGenW, double tPlateC,
                               double areaInsulMm2, double areaBareMm2, double insulThickMm,
                               double tabSectionMm2, double tabLenMm,
                               double holeSectionMm2, double discSpanMm,
                               double tabInsulThickMm = double.NaN,
                               double? clampConductanceWPerK = null)
    {
        var r = new Result { QGenW = qGenW, ClampFromField = clampConductanceWPerK is not null };   // R48 G2 复审二（2026-09-15 Opus 5）：出处位在护栏之前定，判不了时也读得到

        // ★ 护栏：电阻率拟合只在约 0–1400 °C 有效，外面二次项会翻号，
        //   dρe/dT 变负 ⇒ 算出「升温反而少发热」的荒谬结论。
        //   壳解在不稳定几何上会跑到几千度，若拿那个温度来判，得到的全是垃圾
        //   （实测现役几何给 5981 °C ⇒ dP/dT = −3.09）。**判据必须在工作温度上评。**
        const double tFitMaxC = 1400;
        if (tPlateC > tFitMaxC || tPlateC < 0)
        {
            r.Note = $"★ 评估温度 {tPlateC:0} °C 超出铂电阻率拟合区间 [0, {tFitMaxC:0}] —— " +
                     "无法判定。这通常意味着场解本身已发散（几何不可行），请先解决发散。";
            r.DGenDT = double.NaN;
            return r;
        }

        // ── 发热侧：P ∝ ρe(T)，故 dP/dT = P·(1/ρe)(dρe/dT)
        double tcr = Materials.PtTcr(tPlateC);          // 1/K
        r.DGenDT = qGenW * tcr;

        // ── ① 表面：数值微分 q″(T)，两面
        double dT = 5.0;
        double tabIns = double.IsNaN(tabInsulThickMm) ? 0.0 : tabInsulThickMm;
        double QSurf(double t)
            => 2.0 * 1e-6 * (areaInsulMm2 * DesignScreen.PlateFluxWPerM2(p, t, insulThickMm)
                           + areaBareMm2 * DesignScreen.PlateFluxWPerM2(p, t, tabIns));
        r.DSurfDT = (QSurf(tPlateC + dT) - QSurf(tPlateC - dT)) / (2 * dT);

        // ── ② 沿舌片到铜排夹：夹持是定温边界 ⇒ dQ/dT = 导度本身
        //    夹持**温度**高低不影响稳定性，只影响工作点；导度才是稳定器。
        double k = Materials.PtThermalK(tPlateC) * 1e-3;      // W/(mm·K)
        // ★★ 2026-08-28：热导边界（BusbarConductanceWPerK ≥ 0）下 BusbarClampTempC 恒为 −1，
        //   于是**明明有 G 这条实打实的导热通道，DClampDT 却被判成 0**（偏保守，抹掉一个稳定器）。
        //   物理上三种情形分明：
        //    · 定温：铜排是理想热沉 ⇒ 稳定器就是**舌片导度**本身；
        //    · 热导：舌片导度与铜排导度**串联** ⇒ 1/(1/g_舌 + 1/G)；
        //    · 自由端：这条通道不存在 ⇒ 0。
        double gTab = tabLenMm > 1e-6 ? k * tabSectionMm2 / tabLenMm : 0;
        // R48 G2 复审（2026-09-15 Opus 5）：老算法的值总算一份（灵敏度对照，见 Result.DClampGeomDT）；下面 else 支取的就是它，算术不变
        r.DClampGeomDT = p.BusbarClampTempC >= 0
                       ? gTab
                       : (p.BusbarConductanceWPerK >= 0 && gTab > 1e-12
                          ? 1.0 / (1.0 / gTab + 1.0 / Math.Max(1e-12, p.BusbarConductanceWPerK))
                          : 0);
        if (clampConductanceWPerK is double gField)
        {
            // R48 G2（2026-09-15 Opus 5）：稳态场标定的导度（定温边界 = 法兰 → 夹持 × (1 − r)；热导边界 = 法兰 → 压接 → 冷端串联；自由端 = 0），见参数注释
            r.DClampDT = gField;
        }
        else
        {
            r.DClampDT = r.DClampGeomDT;
        }

        // ── ③ 经管孔到管子：管子也是近似定温（由控温维持）
        r.DTubeDT = discSpanMm > 1e-6 ? k * holeSectionMm2 / discSpanMm : 0;

        // 失控前的温升余量：线性外推到 dQ/dT = dP/dT
        // dP/dT 随 T 增长（P∝ρe 且 ρe 增），dQ/dT 随 T 增长更快时才稳定；
        // 这里只给一个量级：(dQ/dT − dP/dT) ÷ (dP/dT · tcr)
        r.HeadroomK = r.DGenDT > 1e-12 && r.Stable
                    ? (r.DLossDT - r.DGenDT) / Math.Max(1e-12, r.DGenDT * tcr)
                    : 0;

        // R48 G2（2026-09-15 Opus 5）：夹持导度标定不出来 ⇒ 判不了，不许落到下面「热失控」那句（NaN 比较恒为假）
        if (double.IsNaN(r.DClampDT))
        {
            r.HeadroomK = 0;
            r.Note = "★ 经舌片流进铜排的导度从稳态场标定不出来 —— 无法判定（不按舌片几何另算一份）";
            return r;
        }
        r.Note = r.Stable
            ? $"稳定：散热随温升增加 {r.DLossDT:0.00} W/K，快于发热的 {r.DGenDT:0.00} W/K"
            : $"★ 热失控：发热随温升增加 {r.DGenDT:0.00} W/K，快于散热的 {r.DLossDT:0.00} W/K —— " +
              "温度会自行往上跑，直到烧断";
        return r;
    }
}
