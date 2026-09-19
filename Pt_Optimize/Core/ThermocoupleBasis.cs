using System;

namespace PtOptimize.Core;

/// <summary>
/// ★ R48 审查第 2 条（2026-09-14，Opus 5）：**法兰「看不见的过热」的热偶读数基准** —— 只有这一份，探针与以后接进判据都调它，不在测试里手抄。
///
/// 出处（用户 2026-09-14）：控温热偶在**段中点**、误差 5 ℃（「10 ℃ 是上下各 5 ℃」）；**共用法兰的基准取两侧热偶读数的对数平均**。
/// 稳态下热偶读数 = 该段控温点（段解按中点 = 设定反算电流，见 SegmentSolver.FindCurrent），所以基准由控温点算：
///   · 端片（只属于一段）：该段控温点；
///   · 共用片（夹在段 j−1 与段 j 之间）：两侧控温点的**对数平均**，在开尔文里取（对数平均对温标的零点不是不变的，
///     绝对温度才有意义；1150/1080 °C 两种取法只差约 0.07 K）。
///
/// ⚠ 2026-09-14 之前 R48SecondBatchTests 与 R48 C 路交接里共用片取的是「两侧设定的**较大值**」—— 不是用户定的口径；
///   B2 带玻璃时 HC2|HC3 片舌区峰 1085.70 °C（deliverable\R48_空管稳态_带玻璃_2026-09-14.txt）：按较大值 1080 是 +5.7 K，
///   按对数平均 1064.94 °C 是 +20.8 K（R48 审查第 2 条），差得出结论的方向。
/// ⚠ 本类**只给基准**，不给限值：物理把关人第八轮提的「最热铂 − 基准 ≤ 5 K」还没写进判据代码、也还要请用户确认，
///   所以这里不放 5，免得读成已采纳。
/// </summary>
public static class ThermocoupleBasis
{
    /// <summary>两个温度（°C）的对数平均 °C，在开尔文里取；两者相等（差 &lt; 1e-9 K）时就是该值。</summary>
    public static double LogMeanC(double aC, double bC)
    {
        double a = aC + 273.15, b = bC + 273.15;
        if (!(a > 0) || !(b > 0)) return double.NaN;
        if (Math.Abs(a - b) < 1e-9) return 0.5 * (aC + bC);
        return (a - b) / Math.Log(a / b) - 273.15;
    }

    /// <summary>
    /// 第 <paramref name="plate"/> 片（0 = 入口端片，segCount = 出口端片，其余为共用片）的热偶读数基准 °C。
    /// <paramref name="setpointC"/> 按段给（长度 = 段数）；片号越界返回 NaN。
    /// </summary>
    public static double ReferenceC(double[] setpointC, int plate)
    {
        if (setpointC is null || setpointC.Length == 0) return double.NaN;
        int n = setpointC.Length;
        if (plate < 0 || plate > n) return double.NaN;
        if (plate == 0) return setpointC[0];
        if (plate == n) return setpointC[n - 1];
        return LogMeanC(setpointC[plate - 1], setpointC[plate]);
    }

    /// <summary>该片最热的铂 °C：圆盘峰与舌区峰取大（<see cref="FlangeOut.TDiscMaxC"/>、<see cref="FlangeOut.TTabMaxC"/>）。</summary>
    public static double HottestPtC(FlangeOut f) => Math.Max(f.TDiscMaxC, f.TTabMaxC);

    /// <summary>「该片最热的铂 − 热偶读数基准」K（正 = 比热偶读数基准热）。</summary>
    public static double HottestMinusReferenceK(LineResult r, int plate)
    {
        if (r?.Flanges is null || plate < 0 || plate >= r.Flanges.Length || r.Segments is null) return double.NaN;
        var sp = new double[r.Segments.Length];
        for (int i = 0; i < sp.Length; i++) sp[i] = r.Segments[i].SetpointC;
        return HottestPtC(r.Flanges[plate]) - ReferenceC(sp, plate);
    }
}
