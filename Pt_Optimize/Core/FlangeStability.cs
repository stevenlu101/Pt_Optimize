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
        /// <summary>评估温度超出物性拟合区间 ⇒ 判不了（而不是「稳定」）</summary>
        public bool Undetermined => double.IsNaN(DGenDT);
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
    public static Result Check(DesignInputs p, double qGenW, double tPlateC,
                               double areaInsulMm2, double areaBareMm2, double insulThickMm,
                               double tabSectionMm2, double tabLenMm,
                               double holeSectionMm2, double discSpanMm)
    {
        var r = new Result { QGenW = qGenW };

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
        double QSurf(double t)
            => 2.0 * 1e-6 * (areaInsulMm2 * DesignScreen.PlateFluxWPerM2(p, t, insulThickMm)
                           + areaBareMm2 * DesignScreen.PlateFluxWPerM2(p, t, 0));
        r.DSurfDT = (QSurf(tPlateC + dT) - QSurf(tPlateC - dT)) / (2 * dT);

        // ── ② 沿舌片到铜排夹：夹持是定温边界 ⇒ dQ/dT = 导度本身
        //    夹持**温度**高低不影响稳定性，只影响工作点；导度才是稳定器。
        double k = Materials.PtThermalK(tPlateC) * 1e-3;      // W/(mm·K)
        r.DClampDT = p.BusbarClampTempC >= 0 && tabLenMm > 1e-6
                   ? k * tabSectionMm2 / tabLenMm : 0;

        // ── ③ 经管孔到管子：管子也是近似定温（由控温维持）
        r.DTubeDT = discSpanMm > 1e-6 ? k * holeSectionMm2 / discSpanMm : 0;

        // 失控前的温升余量：线性外推到 dQ/dT = dP/dT
        // dP/dT 随 T 增长（P∝ρe 且 ρe 增），dQ/dT 随 T 增长更快时才稳定；
        // 这里只给一个量级：(dQ/dT − dP/dT) ÷ (dP/dT · tcr)
        r.HeadroomK = r.DGenDT > 1e-12 && r.Stable
                    ? (r.DLossDT - r.DGenDT) / Math.Max(1e-12, r.DGenDT * tcr)
                    : 0;

        r.Note = r.Stable
            ? $"稳定：散热随温升增加 {r.DLossDT:0.00} W/K，快于发热的 {r.DGenDT:0.00} W/K"
            : $"★ 热失控：发热随温升增加 {r.DGenDT:0.00} W/K，快于散热的 {r.DLossDT:0.00} W/K —— " +
              "温度会自行往上跑，直到烧断";
        return r;
    }
}
