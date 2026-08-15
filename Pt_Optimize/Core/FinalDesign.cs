namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **定案几何的唯一来源**（2026-08-15 建立）。
///
/// 为什么要有这个文件：定案值此前是**各命令各手抄一份**。
/// `--final2` 往前推进之后，`--hotspot` 与 `--busbarplan` 还钉着几代之前的几何
/// （管壁 0.6、舌厚 {1.37,2.02,1.80,1.04}、管保温 10 mm、无管孔环），
/// 它们照样跑得出漂亮的数 —— 但那是**另一个设计**的数。
/// 这正是 HANDOVER §1.8「安静失败」家族的形状：不报错、格式正常、结论错。
///
/// ⇒ 与判据收敛到 <c>LineRunner.Judge</c> 同理：**几何也只能有一个来源**。
///   任何辅助命令要「对着定案构型量」，就从这里取，不许再抄。
///   `--final2` 是**唯一**有权更新这里的地方（它是定尺寸器）。
///
/// ⚠ 改这里之前先想清楚：下面每个数都是某一轮实测收敛的结果，
///   不是可以随手调的参数。改了就要重跑 `--final2` 复核全判据。
/// </summary>
public static class FinalDesign
{
    /// <summary>本组数值出自哪一次运行 —— 报告里要能追溯到源头</summary>
    public const string Provenance =
        "--final2 可行性阶梯 D6（舌厚→③=8K／环倍率→②″=−0.02K／舌保温抗饱和接力）；" +
        "管壁 1.4 mm 是**最薄的全过档**，合计 5245 g（1.2 档 ②″ 差 +0.08，两旋钮均饱和）";

    // ── 管
    public static double WallMm = 1.4;
    public static double TubeInsulMm = 5.0;
    public static readonly double[] SetpointC = { 1150.0, 1080.0, 1050.0 };

    // ── 法兰（四片：入口 / 共用1 / 共用2 / 出口）
    public static double DiscRadiusMm = 30.0;
    public static double TabLengthMm = 90.0;
    public static double TabHalfWidthMm = 15.0;
    public static double TabFilletMm = 3.0;
    public static double[] TabThickMm = { 2.84, 4.75, 4.51, 2.43 };
    public static double[] TabInsulMm = { 18.7, 1.6, 1.4, 3.9 };

    // ── 管孔渐变环：**相对量**（绝对值写法已两次造成安静失败，见 §1.8 ⑥⑦）
    /// <summary>环宽 mm，相对管孔外扩；两级台阶在 孔+w 与 孔+2w</summary>
    public static double RingWidthMm = 3.0;
    /// <summary>内圈厚度倍率（相对板厚）；外圈取 1 + 0.4(μ−1)</summary>
    public static double[] RingMul = { 1.34, 1.22, 1.22, 1.18 };

    // ── 压接
    public static double ClampLengthMm = 40.0;
    public static double ClampTempC = 450.0;

    public static double HoleRadiusMm => WallMm + 25.0;
    public static double[] RingRadiiMm =>
        new[] { HoleRadiusMm + RingWidthMm, HoleRadiusMm + 2 * RingWidthMm };

    /// <summary>按本定案构型造第 j 片（0=入口, 1=共用1, 2=共用2, 3=出口）。</summary>
    public static FlangePlate Plate(int j, double discFloorMm)
    {
        double td = System.Math.Max(TabThickMm[j], discFloorMm);
        double mu = RingMul[j];
        return new FlangePlate
        {
            DiscRadiusMm = DiscRadiusMm, HoleRadiusMm = HoleRadiusMm,
            TabEndXMm = -TabLengthMm, TabEndHalfWidthMm = TabHalfWidthMm,
            ThicknessMm = td,
            DiscStepRadiiMm = RingRadiiMm,
            DiscStepThicknessMm = new[] { td * mu, td * (1 + (mu - 1) * 0.4) },
            TabThicknessMm = double.NaN,
            InsulBoundaryXMm = double.NaN, TabInsulThickMm = TabInsulMm[j],
            TabParallel = true, TabFilletMm = TabFilletMm,
            WeldFilletLegMm = System.Math.Max(td, WallMm)
        };
    }

    public static string Describe() =>
        $"管壁 {WallMm:0.0}／管保温 {TubeInsulMm:0}／盘Ø{2 * DiscRadiusMm:0}／" +
        $"舌 {TabLengthMm:0}×{2 * TabHalfWidthMm:0}／板厚 {string.Join("/", TabThickMm)}／" +
        $"舌保温 {string.Join("/", TabInsulMm)}／" +
        $"环 r≤孔+{RingWidthMm:0}→×{string.Join("/", RingMul)}／" +
        $"压接 {ClampLengthMm:0} 夹 {ClampTempC:0} °C　【{Provenance}】";
}
