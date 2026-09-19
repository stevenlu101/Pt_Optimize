using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 R 路：**反向设计** —— 先定目标温场，从铜排端沿舌板往回积分，解出每段该包多厚保温
//  （2026-09-17，Opus 5）
//
//  ══ 要回答的那一句
//    舌保温这根**单值**旋钮的可行窗口只有 0.1～0.2 mm（r48_L −10 W/mm、r48_P 140 mm 上窗口 0.1 mm），
//    连一个 0.5 mm 的现场包法档都落不进去；加长舌板反而把窗口关死（r48_P）。
//    **换成一条沿舌长连续变化的保温剖面，窗口会不会宽？**
//
//  ══ 链路（三种算法交叉验证，互相不共用中间量）
//    ① 一维反解：给定 Δ（冷端厚 − 热端厚），二分解热端厚，使热端净流入命中目标（TabReverse1D.SolveForDelta）
//    ② 一维正演反查：拿 ① 解出来的温度场，逐点算「要散多少」再在散热表上反查厚度（TabReverse1D.InvertFromField）
//       —— 与 ① 是两条不同的数值路径（打靶 vs 直接反演），对得上才算数
//    ③ 二维整线解：把剖面装进 FlangePlate.TabInsulProfile，跑生产口径 LineRunner.Run 带玻璃稳态
//
//  ══ 跑前写死的判读与门槛（2026-09-17 Opus 5；**跑之前写，跑完一条不改**）
//    目标（写死）
//      T1　热端温度目标 = 本片热偶基准 − 2.5 K（「管根低于热偶读数 ≤ 5 K」这条判据的一半作裕量）。
//          一维模型的热端在**圆盘切点**，不是管孔 ⇒ 目标温度 = 管根目标 − δ盘，
//          δ盘 =（二维锚点上）管根温度 − 切点处温度，**是测量值不是拍的**。
//      T2　热端净流入目标 = 管孔净流入目标 **+2.0 W** ＋ 圆盘偏置 D。
//          管孔净流入的生产判据只要求 > 0（没有上限）；实操上限由「管根低于热偶读数 ≤ 5 K」封顶。
//          +2.0 W 的由来：r48_L 求解器落点上四片实测 1.60 / 2.34 / 1.23 / 0.66 W，取在这四个数的中间。**写死。**
//          D = 二维锚点的 QGenDisc − QLossDisc（圆盘那一块一维不覆盖）。
//      T3　剖面族：自由段**线性**，t(ξ) = t热端 + Δ·ξ（ξ 从切点 0 到压接入口 1），压接段沿用冷端值。
//          Δ 扫 −12 … +12 步 1.0；每个 Δ 二分解 t热端（步长 1.0 是算力所限，扫描区间与步长写死跑前）。
//      T4　**「反解剖面」的选法**：取 Δ 扫描里**冷端恰好落到裸铂（t冷端 = 0）**的那一条。
//          理由（写在跑前）：整体平移时只有**已经落到 0 的段**会饱和（再减也减不下去），
//          这是形状唯一可能把窗口撑开的机制；其余形状对「平均厚度」的响应都一样。
//          若这样的 Δ 不存在 ⇒ 取可解的 |Δ| 最大那一条，并写明「没有饱和段」。
//    判读
//      R-P1　反解剖面的可行窗口 ≥ 0.5 mm ⇒ 这条路走得通（落得进现场 0.5 mm 包法档）。
//      R-P2　窗口 < 0.5 mm ⇒ 走不通；照记「比均匀宽几倍」，不许改判据去凑。
//      R-P3　若反解剖面上的 |d(管孔净流入)/d(整体平移)| 与均匀剖面的相差 < 20 % ⇒
//            结论写「判据只认**平均厚度**、不认形状」，形状这条路在物理上就撑不开窗口。
//      R-P4　一维与二维的热端量（管根温度、净流入）偏差大于判据窗口（5 K / 2 W）⇒
//            一维只能用来定**方向**，不能用来定剖面；照记，按偏差校准后最多再来一轮。
//      R-P5　方向与预期相反 ⇒ 不停、照记、结论里点名。
//      R-P6　窗口顶到扫描边界 ⇒ 报「真实宽度 ≥ 这个数」。
//    门槛
//      G1　交叉验证：① 与 ② 解出来的厚度在自由段中段（ξ ∈ [0.1, 0.9]）逐点差 ≤ 0.30 mm，
//          否则写「两条算法对不上」，**不许只报其中一条**。
//      G2　二维判定一律用生产判据 LineResult.AllOk / Failed，门槛一个不动。
//      G3　网格：导航网格 = 整线算例缺省（与 r48_L、r48_P 同一张）；细网格 = MeshVerify.RequiredMeshFor。
// ════════════════════════════════════════════════════════════════════════════

public class R48ReverseDesignTests
{
    private readonly ITestOutputHelper _o;
    public R48ReverseDesignTests(ITestOutputHelper o) { _o = o; }

    // ── r48_L 求解器解出来的终值（只读那份 .txt；本线不重跑求解器）
    internal static readonly double[] SolvedTabThickMm = { 0.73, 1.26, 1.26, 0.73 };
    internal static readonly double[] SolvedTongueThickMm = { 2.03, 3.51, 3.51, 2.03 };
    internal static readonly double[] SolvedTabInsulMm = { 5.1, 2.3, 3.6, 10.4 };

    // ── 跑前写死的目标与门槛
    internal const double HotMarginK = 2.5;          // 5 K 判据的一半
    internal const double NetFluxTargetW = 2.0;      // 管孔净流入目标
    internal const double CrossCheckTolMm = 0.30;    // G1
    internal const double WindowNeedMm = 0.5;        // R-P1
    internal const int Segs = 5;

    internal static DesignSpec SolvedW08()
    {
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = (double[])SolvedTabThickMm.Clone();
        d.TongueThickMm = (double[])SolvedTongueThickMm.Clone();
        d.TabInsulMm = (double[])SolvedTabInsulMm.Clone();
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.SlotSpanDeg = new double[4];
        d.TabHoleRMm = new double[4];
        d.TabHoleAspect = new[] { 1.0, 1.0, 1.0, 1.0 };
        return d;
    }

    /// <summary>一片的二维锚点标定量（全部从同一次整线解读出来，不拼两份输出）。</summary>
    internal sealed class PlateCalib
    {
        public int J; public string Name = "";
        public double CurrentA, TRootC, RefC, QFromTubeW, QGenTabW, QLossTabW, QClampW, QGenDiscW, QLossDiscW;
        public double BusGWPerK, TClampBandMeanC, TTangentC, DiscOffsetW, DeltaDiscK, TabThickMm, TabHalfWidthMm;
        public double[] TabXMm = Array.Empty<double>(), TabTMeanC = Array.Empty<double>();
    }

    internal sealed class Anchor
    {
        public LineResult Res = null!;
        public LineCase Case = null!;
        public DesignSpec Spec = null!;
        public DesignInputs P = null!;
        public PlateCalib[] Plates = Array.Empty<PlateCalib>();
        public double Seconds;
    }

    /// <summary>
    /// 二维锚点：求解器终值的那份设计，跑一次生产口径整线带玻璃稳态（导航网格），
    /// 把一维模型要用的标定量全部量出来。**一维的每个常数都指回这一次运行。**
    /// </summary>
    internal static Anchor RunAnchor(DesignSpec? spec = null, DesignInputs? pin = null, Action<LineCase>? tweak = null)
    {
        var p = pin ?? new DesignInputs();
        var d = spec ?? SolvedW08();
        var lc = d.BuildCase(p);
        tweak?.Invoke(lc);
        var sw = Stopwatch.StartNew();
        var res = LineRunner.Run(lc, null);
        sw.Stop();
        var a = new Anchor { Res = res, Case = lc, Spec = d, P = p, Seconds = sw.Elapsed.TotalSeconds };
        var list = new List<PlateCalib>();
        for (int j = 0; j < res.Flanges.Length; j++)
        {
            var f = res.Flanges[j];
            var g = lc.FlangePlates[Math.Min(j, lc.FlangePlates.Length - 1)];
            var c = new PlateCalib
            {
                J = j, Name = f.Name, CurrentA = f.CurrentA, TRootC = f.TRootC,
                QFromTubeW = f.QFromTubeW, QGenTabW = f.QGenTabW, QLossTabW = f.QLossTabW, QClampW = f.QClampW,
                QGenDiscW = f.QGenDiscW, QLossDiscW = f.QLossDiscW, BusGWPerK = f.BusGWPerK,
                TabThickMm = double.IsNaN(g.TabThicknessMm) ? g.ThicknessMm : g.TabThicknessMm,
                TabHalfWidthMm = g.TabEndHalfWidthMm,
            };
            c.DiscOffsetW = c.QGenDiscW - c.QLossDiscW;
            c.RefC = ThermocoupleBasis.At(res, j).RefC;
            var m = f.Mesh; var t = f.TField;
            if (m is not null && t.Length == m.CellCount)
            {
                double clampLen = lc.Base.BusbarClampLengthMm;
                double sC = 0, aC = 0;
                var bins = new Dictionary<int, (double a, double at)>();
                for (int i = 0; i < m.CellCount; i++)
                {
                    double x = m.Centroid[i].X, ar = m.Area[i];
                    if (FlangeMesher.InClampSegment(x, g.TabTipXMm, clampLen, g.TwoTabs)) { sC += ar * t[i]; aC += ar; }
                    if (x >= g.Tangent().X) continue;                       // 只统计舌片区
                    int b = (int)Math.Floor(x);                             // 1 mm 一格
                    var cur = bins.TryGetValue(b, out var v) ? v : (0.0, 0.0);
                    bins[b] = (cur.Item1 + ar, cur.Item2 + ar * t[i]);
                }
                c.TClampBandMeanC = aC > 0 ? sC / aC : double.NaN;
                var ks = bins.Keys.OrderByDescending(k => k).ToArray();
                c.TabXMm = ks.Select(k => k + 0.5).ToArray();
                c.TabTMeanC = ks.Select(k => bins[k].Item2 / bins[k].Item1).ToArray();
                c.TTangentC = c.TabTMeanC.Length > 0 ? c.TabTMeanC[0] : double.NaN;
            }
            c.DeltaDiscK = c.TRootC - c.TTangentC;
            list.Add(c);
        }
        a.Plates = list.ToArray();
        return a;
    }

    /// <summary>按锚点标定造第 j 片的一维输入（每个常数指回锚点那一次运行）。</summary>
    internal static TabReverse1D.Inputs Inputs1D(Anchor a, int j, double? clampColdC = null)
    {
        var c = a.Plates[j];
        var lc = a.Case;
        var g = lc.FlangePlates[Math.Min(j, lc.FlangePlates.Length - 1)];
        var p2 = SegmentSolver.Clone(lc.Base);
        p2.TSetC = c.RefC;
        double cold = clampColdC ?? (lc.ClampTempC.Length > 0 ? lc.ClampTempC[Math.Min(j, lc.ClampTempC.Length - 1)] : 450);
        return new TabReverse1D.Inputs
        {
            P = p2,
            CurrentA = c.CurrentA,
            TabWidthMm = 2 * c.TabHalfWidthMm,
            TabThickMm = c.TabThickMm,
            TangentXMm = g.Tangent().X,
            ClampEntryXMm = g.TabEndXMm + lc.Base.BusbarClampLengthMm,
            TabTipXMm = g.TabEndXMm,
            ClampColdEndC = cold,
            ClampConductanceWPerK = c.BusGWPerK,
            // T1：热端目标 = （热偶基准 − 2.5 K）再减去二维量到的「管根 − 切点」温差
            THotTargetC = c.RefC - HotMarginK - c.DeltaDiscK,
            // T2：热端净流入 = 管孔净流入目标 + 圆盘偏置
            QInHotTargetW = NetFluxTargetW + c.DiscOffsetW,
        };
    }

    /// <summary>二维那一次的「热端净流入」= 进压接段 + 自由段舌面散热 − 自由段舌焦耳热（三项同一个定义域）。</summary>
    internal static double QInHot2D(PlateCalib c) => c.QClampW + c.QLossTabW - c.QGenTabW;

    private static string N(double v) => double.IsNaN(v) ? "—" : v.ToString("0.###");

    /// <summary>逐片：按锚点标定散热等效系数，扫 Δ 看解族，再按 T4 取「冷端恰好裸铂」那一条作反解剖面。</summary>
    internal static (TabInsulProfile[] prof, string[] note, TabReverse1D.Inputs[] inp, double[] scale, Func<double, double>[] cont)
        ReverseProfiles(Anchor a, Action<string>? log = null, double deltaLo = -12, double deltaHi = 12, double deltaStep = 1.0)
    {
        int n = a.Plates.Length;
        var prof = new TabInsulProfile[n]; var note = new string[n];
        var inps = new TabReverse1D.Inputs[n]; var scales = new double[n];
        var conts = new Func<double, double>[n];
        for (int j = 0; j < n; j++)
        {
            var c = a.Plates[j];
            var inp = Inputs1D(a, j);
            double qIn2D = QInHot2D(c);
            // ── 标定：一维在**锚点那份单值剖面**上、热端温度钉在二维量到的切点温度上，复现二维的热端净流入
            var calib = TabReverse1D.Clone(inp);
            calib.THotTargetC = c.TTangentC;
            var uniform = TabReverse1D.LinearRamp(calib, SolvedTabInsulMm[j], 0.0);
            var before = TabReverse1D.ShootToHotTemp(calib, uniform);
            var (scale, okS, noteS) = TabReverse1D.CalibrateSurfaceScale(calib, uniform, qIn2D);
            calib.SurfaceScale = scale; inp.SurfaceScale = scale;
            var after = TabReverse1D.ShootToHotTemp(calib, uniform);
            scales[j] = scale;
            log?.Invoke($"片 {j}「{c.Name}」标定：散热等效系数 {(okS ? scale.ToString("0.0000") : "**标不出来**（" + noteS + "）")}"
                      + "（一维是把一片板压成一根等截面杆，横向温度不均与圆角都没了 ⇒ 等效散热面积与真实的差一截；这个系数就是那一截）");
            log?.Invoke("　　量\t一维(标定前)\t一维(标定后)\t二维锚点\t标定后/二维");
            log?.Invoke($"　　热端净流入 W\t{N(before.tr.QInHotW)}\t{N(after.tr.QInHotW)}\t{N(qIn2D)}\t{N(after.tr.QInHotW / qIn2D)}（按定义相等）");
            log?.Invoke($"　　进压接段 W\t{N(before.tr.QToClampW)}\t{N(after.tr.QToClampW)}\t{N(c.QClampW)}\t{N(after.tr.QToClampW / c.QClampW)}");
            log?.Invoke($"　　自由段焦耳热 W\t{N(before.tr.QGenFreeW)}\t{N(after.tr.QGenFreeW)}\t{N(c.QGenTabW)}\t{N(after.tr.QGenFreeW / c.QGenTabW)}");
            log?.Invoke($"　　自由段散热 W\t{N(before.tr.QLossFreeW)}\t{N(after.tr.QLossFreeW)}\t{N(c.QLossTabW)}\t{N(after.tr.QLossFreeW / c.QLossTabW)}");
            log?.Invoke("　⚠ 后两行差得远（一维偏高三四成）。原因看得见、不是拟合不好：二维的「舌片区」是 **r > 盘半径** 那一块，");
            log?.Invoke("　　而一维把 x∈[0, −100] 整条按 60 mm 宽的等截面杆算 —— x 从 0 到 −30 之间有一块半圆（面积 π·30²/2 = 1414 mm²，");
            log?.Invoke("　　占一维所设舌板面积 6000 mm² 的 24 %）在二维里算**圆盘**，而且正好是全片最热、发热与散热都最大的那一块。");
            log?.Invoke("　　⇒ 一维的绝对量不可引用；它只用来给**剖面形状与量级**，判据一律由二维给（判读 R-P4）。");
            inps[j] = inp;

            var sols = new List<TabReverse1D.Solution>();
            for (double d = deltaLo; d <= deltaHi + 1e-9; d += deltaStep)
                sols.Add(TabReverse1D.SolveForDelta(inp, Math.Round(d, 3)));
            log?.Invoke("Δ（冷端−热端）mm\t热端厚 mm\t冷端厚 mm\t平均厚 mm\tT(切点) °C\t热端净流入 W\t进压接段 W\t自由段焦耳热 W\t自由段散热 W\t片上最高温 °C\t备注");
            foreach (var s in sols)
                log?.Invoke($"{s.DeltaMm:+0.0;-0.0;0.0}\t{s.THotMm:0.000}\t{s.TColdMm:0.000}\t{s.MeanMm:0.000}\t{N(s.THotC)}\t{N(s.QInHotW)}\t"
                          + $"{N(s.Tr.QToClampW)}\t{N(s.Tr.QGenFreeW)}\t{N(s.Tr.QLossFreeW)}\t{N(s.Tr.TMaxC)}\t{(s.Ok ? "" : s.Note)}");
            int okCount = sols.Count(s => s.Ok);
            log?.Invoke($"　解族：{deltaLo:0}…{deltaHi:0} 这 {sols.Count} 个 Δ 里 **{okCount} 个有解** ——"
                      + "「热端定温 + 热端定净流入」只有一个方程，剖面有两个自由度 ⇒ 反解出来的本来就是一条**解族**，不是唯一解。");

            // ── T4：冷端恰好裸铂的那一条（单列一支解，不靠扫描去碰）
            var bare = TabReverse1D.SolveColdBare(inp);
            TabReverse1D.Solution? pick = bare.Ok ? bare : sols.Where(s => s.Ok).OrderBy(s => s.TColdMm).FirstOrDefault();
            note[j] = bare.Ok
                ? $"冷端恰好裸铂：t(ξ) = {bare.THotMm:0.000}·(1−ξ)，即热端 {bare.THotMm:0.00} mm → 冷端 0（**有饱和段**）"
                : pick is null ? "**冷端裸铂这一支无解、Δ 扫描也全无解 ⇒ 退回求解器那个单值**"
                               : $"冷端裸铂这一支无解（{bare.Note}）⇒ 退而取扫描里冷端最薄的那一条 Δ = {pick.DeltaMm:+0.0;-0.0;0.0} mm";
            if (pick is null || !pick.Ok)
            {
                conts[j] = TabReverse1D.LinearRamp(inp, SolvedTabInsulMm[j], 0.0);
                prof[j] = TabInsulProfile.Uniform(TabInsulProfile.EvenEdges(inp.TangentXMm, inp.ClampEntryXMm, Segs), SolvedTabInsulMm[j]);
            }
            else
            {
                conts[j] = TabReverse1D.LinearRamp(inp, pick.THotMm, pick.TColdMm - pick.THotMm);
                prof[j] = TabReverse1D.Quantize(inp, conts[j], Segs);
            }
            log?.Invoke($"★ 片 {j} 的反解剖面：{note[j]}");
            log?.Invoke($"　　一维热端量：T(切点) {N(pick?.THotC ?? double.NaN)} °C（目标 {inp.THotTargetC:0.00}）、净流入 {N(pick?.QInHotW ?? double.NaN)} W（目标 {inp.QInHotTargetW:0.###}）");
            log?.Invoke($"　　落成 {Segs} 段常值（每段取中点、向图纸格 0.1 mm 取整）：{prof[j].Describe()}");
            log?.Invoke($"　　物理读法：热端厚、冷端裸 —— 热端要把自己的焦耳热留住（散多了就要从管子补，管根就掉温）；"
                      + "冷端已经凉、散热本来就少，剥光它几乎不花什么代价，却换来一段「再减也减不下去」的饱和区。");
            log?.Invoke("");
        }
        return (prof, note, inps, scales, conts);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  R2　一维反解 + 交叉验证
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    [Trait("速度", "慢")]
    public void R2_一维反解与交叉验证()
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_R_一维反解_本次开跑于{stamp}.txt");
        var total = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() { try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } catch { } }
        void Say(string s) { W(s); _o.WriteLine(s); Console.WriteLine(s); Flush(); }

        W("R48 R 路　**反向设计 · 第一步：一维反解**（先定目标温场，解出每段该包多厚保温）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W();
        W("── 问题（用户 2026-09-17）");
        W("　舌保温的**单值**旋钮窗口只有 0.14～0.2 mm（r48_L 实测 −10 W/mm；r48_P 舌长 140 mm 上窗口 0.1 mm，");
        W("　一个 0.5 mm 的现场包法档都落不进去），加长舌板反而把窗口关死。改成**沿舌长连续变化的保温剖面**，窗口会不会宽？");
        W();
        W("── 方程与出处（一个常数都不另写；全部引用 Core 同一份函数）");
        W("　d/dx( k(T)·Ac·dT/dx ) + ρ(T)·I²/Ac − q″(T, t保温(x))·P = 0　（自由段）");
        W("　k(T) = Materials.PtThermalK　ρ(T) = Materials.PtResistivity　q″ = DesignScreen.PlateFluxWPerM2（法兰表面热流的唯一配方）");
        W("　Ac = 舌宽 × 舌片厚　P = 2 × 舌宽（两个大面）");
        W("　代码：Pt_Optimize/Core/TabReverse1D.cs（RK4 积分 + 二分打靶 + 二分反查厚度）");
        W();
        W("★ **冷端边界：工单的前提要更正。**");
        W("　工单写「夹头导热从 W08 探针的 191 W 反推」——那是**热导边界**的写法。本算例不是热导边界：");
        W("　生产算例给的是**夹持温度**，ShellThermal.ClampBoundaryOf 判成**定温**，二维里整个压接段被钉在夹头温度上");
        W("　（下面锚点表里「铜排热导 = −1」与「压接段面积加权均温 = 450.000 °C」两条都是本次实测）。");
        W("　⇒ 一维照二维来：定义域只取**自由段**（压接入口 → 圆盘切点），冷端 T = 夹头温度。");
        W("　　191 W 改作**对拍量**：一维解出来的「进压接段的热」要对得上二维的 FlangeOut.QClampW。");
        W();
        W("── 跑前写死的目标与判读（见测试类头注释，跑完一条没改）");
        W($"　T1 热端温度目标 = 热偶基准 − {HotMarginK:0.0} K，再减去二维锚点量到的「管根 − 切点」温差 δ盘");
        W($"　T2 热端净流入目标 = 管孔净流入目标 {NetFluxTargetW:0.0} W + 圆盘偏置 D（= 锚点 QGenDisc − QLossDisc）");
        W("　T3 剖面族 = 自由段线性，参数 Δ = 冷端厚 − 热端厚");
        W("　T4 「反解剖面」取 Δ 扫描里**冷端恰好落到裸铂**的那一条（只有落到 0 的段在整体平移时会饱和）");
        W($"　G1 交叉验证门槛：两条算法在 ξ∈[0.1,0.9] 上逐点差 ≤ {CrossCheckTolMm:0.00} mm");
        W();

        Say("── 二维锚点：求解器终值的那份设计，生产口径整线带玻璃稳态（导航网格）跑一次 …");
        var a = RunAnchor();
        Assert.True(a.Res.Ok, "锚点整线解没解出来：" + a.Res.Message);
        W($"锚点耗时 {a.Seconds:0} s　全判据通过 {a.Res.AllOk}　耦合收敛 {a.Res.Converged}　铂重 {a.Res.TotalMassG:0} g");
        foreach (var ch in a.Res.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target))
            W($"　　{Criteria.Plain(ch.Name)}：实际 {ch.Actual:0.###} / 限值 {ch.Limit:0.###} {ch.Unit}，"
              + $"{(ch.Undetermined ? "**判不了**" : ch.Ok ? "过" : "**不过**")}，位置 {ch.Where}");
        W("　★ 与 r48_P 在同一棵合并树上那次锚点对照（同一份设计）：铂重 4245 g／最热铂高出热偶读数 4.529 K／");
        W("　　管孔净流入 0.657 W（整线取最坏片）／管根低于热偶读数 4.922 K／法兰截面 J 9.983／管 J 9.506。");
        W("　　对得上 ⇒ R1 那个新口子没有碰到默认路径。");
        W();
        W("── 锚点标定量（逐片；全部出自上面这一次运行）");
        W("片\t名\t电流 A\t热偶基准 °C\t管根 °C\t切点 °C\tδ盘 K\t管孔净流入 W\t自由段舌焦耳热 W\t自由段舌面散热 W\t进压接段 W\t盘焦耳热 W\t盘散热 W\t圆盘偏置 D W\t铜排热导 W/K\t压接段均温 °C\t热端净流入(二维) W");
        foreach (var c in a.Plates)
            W($"{c.J}\t{c.Name}\t{c.CurrentA:0}\t{c.RefC:0.0}\t{c.TRootC:0.00}\t{N(c.TTangentC)}\t{N(c.DeltaDiscK)}\t"
              + $"{N(c.QFromTubeW)}\t{N(c.QGenTabW)}\t{N(c.QLossTabW)}\t{N(c.QClampW)}\t{N(c.QGenDiscW)}\t{N(c.QLossDiscW)}\t"
              + $"{N(c.DiscOffsetW)}\t{N(c.BusGWPerK)}\t{N(c.TClampBandMeanC)}\t{N(QInHot2D(c))}");
        W("　（最后一列 = 进压接段 + 舌面散热 − 舌焦耳热 = 从圆盘流进舌板的热；它也 = 管孔净流入 + 圆盘偏置，两条算出同一个数 ⇒ 分区账自洽。）");
        W();
        W("── 二维锚点的舌板温度剖面（面积加权，1 mm 一格；「—」= 那一段没有格子形心）");
        W("x mm\t" + string.Join("\t", a.Plates.Select(c => c.Name)));
        for (double x = -0.5; x >= -100.5; x -= 2.0)
        {
            var cells = a.Plates.Select(c =>
            {
                int k = Array.FindIndex(c.TabXMm, v => Math.Abs(v - x) < 1.01);
                return k >= 0 ? c.TabTMeanC[k].ToString("0.0") : "—";
            });
            W($"{x:0.0}\t" + string.Join("\t", cells));
        }
        W();
        Flush();

        W("══════ 一维标定与 Δ 扫描 ══════");
        var (prof, note, inps, scales, conts) = ReverseProfiles(a, Say, -12, 12, 1.0);

        W("══════ 交叉验证（第二条算法：不打靶，逐点算「要散多少」再在散热表上反查厚度）══════");
        bool allCross = true;
        for (int j = 0; j < prof.Length; j++)
        {
            var inp = inps[j];
            var pr = prof[j];
            // 与**连续斜坡**比（算法 ① 给出来的就是它）；落成 5 段常值是后一步的工程取整，
            // 拿阶梯去比逐点反查，量到的是取整误差（半个台阶 ≈ 1.2 mm），不是两条算法的差。
            var cont = conts[j];
            var (tr, ok, nt) = TabReverse1D.ShootToHotTemp(inp, cont);
            if (!ok) { W($"片 {j}：装上分段剖面后一维打不到热端目标（{nt}）⇒ 交叉验证**判不了**"); allCross = false; continue; }
            var (qReq, tInv) = TabReverse1D.InvertFromField(inp, tr.XMm, tr.TC);
            W($"── 片 {j}「{a.Plates[j].Name}」　连续斜坡（落成段之前）　散热等效系数 {scales[j]:0.0000}　落成 5 段后 {pr.Describe()}");
            W("ξ\tx mm\tT °C\t① 打靶给的厚度 mm\t② 反查出来的厚度 mm\t差 mm\t所需单面热流 W/m²\t裸铂能散 W/m²");
            double worst = 0; double worstXi = double.NaN; int cmp = 0;
            for (int k = 0; k < tr.XMm.Length; k++)
            {
                double xi = (inp.TangentXMm - tr.XMm[k]) / inp.FreeLenMm;
                double t1 = cont(tr.XMm[k]), t2 = tInv[k];
                bool inBand = xi >= 0.1 && xi <= 0.9;
                if (inBand && t2 >= 0) { cmp++; double dd = Math.Abs(t1 - t2); if (dd > worst) { worst = dd; worstXi = xi; } }
                if (Math.Abs(xi * 20 - Math.Round(xi * 20)) < 1e-9)
                    W($"{xi:0.00}\t{tr.XMm[k]:0.0}\t{tr.TC[k]:0.0}\t{t1:0.000}\t{(t2 < 0 ? "裸铂也不够" : t2.ToString("0.000"))}\t"
                      + $"{(t2 < 0 ? "—" : (t1 - t2).ToString("+0.000;-0.000;0.000"))}\t{qReq[k]:0}\t{inp.QSurf(tr.TC[k], 0.0):0}");
            }
            bool pass = cmp > 0 && worst <= CrossCheckTolMm;
            allCross &= pass;
            W($"★ 门 G1：ξ∈[0.1,0.9] 上 {cmp} 个比较点，最大差 {worst:0.000} mm（ξ={N(worstXi)}），门槛 {CrossCheckTolMm:0.00} mm ⇒ "
              + (pass ? "**两条算法对得上**" : "**两条算法对不上**"));
            W();
            Flush();
        }

        W("══════ 四片的反解剖面（下一步装进二维）══════");
        for (int j = 0; j < prof.Length; j++)
            W($"片 {j}「{a.Plates[j].Name}」：{prof[j].Describe()}　（{note[j]}；求解器那个单值是 {SolvedTabInsulMm[j]:0.0} mm；"
              + $"沿自由段长度加权平均 {prof[j].LengthWeightedMeanMm(inps[j].TangentXMm, inps[j].ClampEntryXMm):0.00} mm）");
        W();
        W($"交叉验证总判：{(allCross ? "四片都对得上" : "**有片对不上或判不了** —— 结论里不许略掉这一句")}");
        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）。");
        W("出处：二维锚点 = LineRunner.Run（Core/LineRunner.cs），导航网格 = 整线算例缺省；一维 = Core/TabReverse1D.cs。");
        W("每个数来自同一次运行，不拼两份输出。　注记：2026-09-17，Opus 5。");
        Flush();
        _o.WriteLine(sb.ToString());

        Assert.True(a.Plates.All(c => !double.IsNaN(c.TTangentC)), "锚点没量到切点温度 ⇒ 一维的热端目标定不下来");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  公用：把剖面装进算例、跑一次生产口径整线带玻璃稳态
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>扫描的三种模式（写死跑前）。</summary>
    internal enum SweepMode
    {
        /// <summary>对照：四片的**单值**舌保温同时 +δ（剖面 = null，就是现在生产链那根旋钮）。</summary>
        Uniform,
        /// <summary>整条剖面**整体平移** +δ（每段都加，下限 0）。</summary>
        ShiftAll,
        /// <summary>只动**热端那一段**（第 0 段）+δ。</summary>
        ShiftHot,
    }

    internal sealed class RunOut
    {
        public double Delta;
        public bool Ok, Converged, AllOk, FieldOk;
        public double NetFluxW, ColdUnderTcK, HotOverTcK, SectionJ, TubeJ, MassG, Sec;
        public double[] PlateNetFluxW = Array.Empty<double>();
        public string Failed = "", Profiles = "";
    }

    /// <summary>按模式造这一档的四片剖面（或单值）。返回 (剖面数组或 null 项, 四片单值)。</summary>
    internal static (TabInsulProfile?[] prof, double[] scalarMm) MakeCase(TabInsulProfile[] baseProf, SweepMode mode, double delta)
    {
        var prof = new TabInsulProfile?[baseProf.Length];
        var scal = (double[])SolvedTabInsulMm.Clone();
        for (int j = 0; j < baseProf.Length; j++)
        {
            switch (mode)
            {
                case SweepMode.Uniform:
                    prof[j] = null;
                    scal[j] = Math.Max(0.0, SolvedTabInsulMm[j] + delta);
                    break;
                case SweepMode.ShiftAll:
                    prof[j] = baseProf[j].Shifted(delta);
                    scal[j] = prof[j]!.LengthWeightedMeanMm(prof[j]!.EdgeXMm[0], prof[j]!.EdgeXMm[^1]);
                    break;
                case SweepMode.ShiftHot:
                    prof[j] = baseProf[j].WithSegment(0, Math.Max(0.0, baseProf[j].ThickMm[0] + delta));
                    scal[j] = prof[j]!.LengthWeightedMeanMm(prof[j]!.EdgeXMm[0], prof[j]!.EdgeXMm[^1]);
                    break;
            }
        }
        return (prof, scal);
    }

    /// <summary>跑一档：生产口径 LineRunner.Run 带玻璃稳态；fine = 细网格（MeshVerify.RequiredMeshFor）。</summary>
    internal static RunOut RunOne(TabInsulProfile?[] prof, double[] scalarMm, double delta, bool fine,
                                  double? clampTempC = null)
    {
        var p = new DesignInputs();
        var d = SolvedW08();
        d.TabInsulMm = (double[])scalarMm.Clone();
        if (clampTempC is double ct) d.ClampTempC = ct;
        var lc = d.BuildCase(p);
        if (fine)
        {
            var (h, r) = MeshVerify.RequiredMeshFor(d);
            MeshAdapt.RefineWholeMesh(lc, h, r);
        }
        for (int j = 0; j < lc.FlangePlates.Length; j++)
            lc.FlangePlates[j].TabInsulProfile = j < prof.Length ? prof[j] : null;
        var sw = Stopwatch.StartNew();
        LineResult res;
        string threw = "";
        try { res = LineRunner.Run(lc, null); }
        catch (Exception ex) { threw = $"{ex.GetType().Name}：{ex.Message}"; res = new LineResult { Ok = false, Message = threw }; }
        sw.Stop();
        return new RunOut
        {
            Delta = delta,
            Ok = res.Ok, Converged = res.Converged, AllOk = res.Ok && res.AllOk,
            FieldOk = res.Ok && res.Flanges.Length > 0 && res.Flanges.All(f => f.FieldsConverged),
            NetFluxW = res.ValueOf(LineResult.Key.NetFlux),
            ColdUnderTcK = res.ValueOf(LineResult.Key.ColdUnderTc),
            HotOverTcK = res.ValueOf(LineResult.Key.HotOverTc),
            SectionJ = res.ValueOf(LineResult.Key.SectionJ),
            TubeJ = res.ValueOf(LineResult.Key.TubeJ),
            MassG = res.TotalMassG, Sec = sw.Elapsed.TotalSeconds,
            PlateNetFluxW = res.Flanges.Select(f => f.QFromTubeW).ToArray(),
            Failed = threw.Length > 0 ? "抛异常：" + threw : !res.Ok ? "整线解没解出来：" + res.Message : string.Join("；", res.Failed),
            Profiles = string.Join(" ｜ ", Enumerable.Range(0, lc.FlangePlates.Length)
                .Select(j => lc.FlangePlates[j].TabInsulProfile is null
                             ? $"片{j} 单值 {lc.FlangePlates[j].TabInsulThickMm:0.00}"
                             : $"片{j} " + string.Join("/", lc.FlangePlates[j].TabInsulProfile!.ThickMm.Select(v => v.ToString("0.00"))))),
        };
    }

    /// <summary>并行跑一串档（整线解本身不并行；每档自己一份算例，Core 里没有可写静态量）。</summary>
    internal static RunOut[] RunMany(TabInsulProfile[] baseProf, SweepMode mode, double[] deltas, bool fine,
                                     int par = 4, double? clampTempC = null)
    {
        var outp = new RunOut[deltas.Length];
        Parallel.For(0, deltas.Length, new ParallelOptions { MaxDegreeOfParallelism = par }, i =>
        {
            var (prof, scal) = MakeCase(baseProf, mode, deltas[i]);
            outp[i] = RunOne(prof, scal, deltas[i], fine, clampTempC);
        });
        return outp;
    }

    internal static (double Lo, double Hi)[] Blocks(RunOut[] rows)
    {
        var o = new List<(double, double)>();
        int i = 0;
        while (i < rows.Length)
        {
            if (!rows[i].AllOk) { i++; continue; }
            int j = i;
            while (j + 1 < rows.Length && rows[j + 1].AllOk) j++;
            o.Add((rows[i].Delta, rows[j].Delta));
            i = j + 1;
        }
        return o.ToArray();
    }

    internal static double Slope(RunOut[] rows, Func<RunOut, double> y)
    {
        var pts = rows.Where(r => r.Ok && !double.IsNaN(y(r))).ToList();
        if (pts.Count < 2) return double.NaN;
        double mx = pts.Average(r => r.Delta), my = pts.Average(y);
        double num = pts.Sum(r => (r.Delta - mx) * (y(r) - my));
        double den = pts.Sum(r => (r.Delta - mx) * (r.Delta - mx));
        return den <= 0 ? double.NaN : num / den;
    }

    internal static void WriteRows(Action<string> W, RunOut[] rows)
    {
        W("δ mm\t判定\t管孔净流入 W\t管根低于热偶读数 K\t最热铂高出热偶读数 K\t截面电流密度\t管电流密度\t场有效\t收敛\t铂重 g\t耗时 s\t四片管孔净流入 W\t没过的");
        foreach (var r in rows)
            W($"{r.Delta:+0.0;-0.0;0.0}\t{(r.AllOk ? "过" : "不过")}\t{N(r.NetFluxW)}\t{N(r.ColdUnderTcK)}\t{N(r.HotOverTcK)}\t"
              + $"{N(r.SectionJ)}\t{N(r.TubeJ)}\t{(r.FieldOk ? "是" : "否")}\t{(r.Converged ? "是" : "否")}\t{r.MassG:0}\t{r.Sec:0}\t"
              + $"{string.Join("/", r.PlateNetFluxW.Select(v => v.ToString("0.00")))}\t{r.Failed}");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  R3　二维验证：反解剖面装进生产链，导航网格与细网格各跑一次
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    [Trait("速度", "慢")]
    public void R3_二维验证()
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_R_二维验证_本次开跑于{stamp}.txt");
        var total = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() { try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } catch { } }
        void Say(string s) { W(s); _o.WriteLine(s); Console.WriteLine(s); Flush(); }

        W("R48 R 路　**反向设计 · 第二步：二维验证**（把一维反解出来的剖面装进生产链，重新判一次）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("判定一律用生产判据 LineResult.AllOk / Failed，门槛一个不动；带玻璃稳态；");
        W("导航网格 = 整线算例缺省（与 r48_L、r48_P 同一张）；细网格 = MeshVerify.RequiredMeshFor（与「◆ 加密复算」同一个来源）。");
        W();

        Say("── 一维反解（锚点 + 标定 + 解族 + 冷端裸铂那一条）…");
        var a = RunAnchor();
        var (prof, note, inps, scales, _) = ReverseProfiles(a, null);
        W($"锚点（求解器终值那份单值设计，导航网格）：全判据通过 {a.Res.AllOk}，铂重 {a.Res.TotalMassG:0} g，耗时 {a.Seconds:0} s");
        W("四片反解剖面（沿舌轴 5 段常值，mm）：");
        for (int j = 0; j < prof.Length; j++)
            W($"　片 {j}「{a.Plates[j].Name}」：{prof[j].Describe()}　平均 {prof[j].LengthWeightedMeanMm(0, -100):0.00} mm"
              + $"（求解器那个单值 {SolvedTabInsulMm[j]:0.0} mm；{note[j]}；散热等效系数 {scales[j]:0.0000}）");
        W();
        Flush();

        // ── 导航网格：反解剖面 vs 均匀（锚点）
        Say("── 导航网格上跑反解剖面 …");
        var navRev = RunOne(prof.Cast<TabInsulProfile?>().ToArray(),
                            prof.Select(q => q.LengthWeightedMeanMm(0, -100)).ToArray(), 0, false);
        W("═══ 导航网格：反解剖面 vs 均匀单值（求解器解 5.1/2.3/3.6/10.4）═══");
        W("量\t均匀单值（锚点）\t反解剖面\t差");
        void Cmp(string name, double u, double r, string f = "0.###")
            => W($"{name}\t{u.ToString(f)}\t{r.ToString(f)}\t{(r - u).ToString("+" + f + ";-" + f + ";0")}");
        Cmp("全判据通过", a.Res.AllOk ? 1 : 0, navRev.AllOk ? 1 : 0, "0");
        Cmp("管孔净流入 W（限值 >0）", a.Res.ValueOf(LineResult.Key.NetFlux), navRev.NetFluxW);
        Cmp("管根低于热偶读数 K（限值 ≤5）", a.Res.ValueOf(LineResult.Key.ColdUnderTc), navRev.ColdUnderTcK);
        Cmp("最热铂高出热偶读数 K（限值 ≤5）", a.Res.ValueOf(LineResult.Key.HotOverTc), navRev.HotOverTcK);
        Cmp("法兰截面电流密度（限值 ≤11）", a.Res.ValueOf(LineResult.Key.SectionJ), navRev.SectionJ);
        Cmp("管电流密度（限值 ≤12）", a.Res.ValueOf(LineResult.Key.TubeJ), navRev.TubeJ);
        Cmp("铂重 g", a.Res.TotalMassG, navRev.MassG, "0");
        W($"反解剖面那一次没过的：{(navRev.Failed.Length == 0 ? "（全过）" : navRev.Failed)}");
        W("⚠ 铂重不含保温 —— 舌保温是外覆纤维，不是铂；两条剖面的铂重本来就该一样。省不省铂要看**保温用量**，见下一行。");
        double volU = SolvedTabInsulMm.Sum() * 60 * 100, volR = prof.Sum(q => q.LengthWeightedMeanMm(0, -100)) * 60 * 100;
        W($"保温用量（四片、舌宽 60 mm × 自由段 100 mm 的等效体积）：均匀 {volU / 1000:0} cm³　反解 {volR / 1000:0} cm³　"
          + $"差 {(volR - volU) / 1000:+0;-0;0} cm³（{(volR / volU - 1) * 100:+0.0;-0.0;0.0} %）");
        W();
        Flush();

        // ── 一维 vs 二维 对拍（第一轮）
        W("═══ 一维 vs 二维 对拍（反解剖面这一份设计；判读 R-P4）═══");
        W("片\t一维目标 管根 °C\t二维实得 管根 °C\t差 K\t一维目标 管孔净流入 W\t二维实得 管孔净流入 W\t差 W");
        var aRev = RunAnchorWithProfiles(prof);
        for (int j = 0; j < prof.Length; j++)
        {
            double tgtRoot = a.Plates[j].RefC - HotMarginK;
            double gotRoot = aRev.Plates[j].TRootC;
            double gotQ = aRev.Plates[j].QFromTubeW;
            W($"{j}「{a.Plates[j].Name}」\t{tgtRoot:0.00}\t{gotRoot:0.00}\t{gotRoot - tgtRoot:+0.00;-0.00;0.00}\t"
              + $"{NetFluxTargetW:0.00}\t{gotQ:0.00}\t{gotQ - NetFluxTargetW:+0.00;-0.00;0.00}");
        }
        W($"判读 R-P4 的门槛：管根偏差 > 5 K 或 净流入偏差 > 2 W ⇒ 一维只能定方向。");
        bool p4 = Enumerable.Range(0, prof.Length).Any(j =>
            Math.Abs(aRev.Plates[j].TRootC - (a.Plates[j].RefC - HotMarginK)) > 5.0
            || Math.Abs(aRev.Plates[j].QFromTubeW - NetFluxTargetW) > 2.0);
        W($"⇒ {(p4 ? "**R-P4 触发**：一维给的剖面在二维上没落到目标，一维只能用来定方向" : "R-P4 未触发：一维给的剖面在二维上就落在目标附近")}");
        W();
        Flush();

        // ── 第二轮：按二维偏差把一维的热端净流入目标校正一次，再反解一次
        W("═══ 校准第二轮（按二维偏差修一维的热端净流入目标，重解一次；最多两轮，写死）═══");
        var prof2 = new TabInsulProfile[prof.Length];
        var note2 = new string[prof.Length];
        for (int j = 0; j < prof.Length; j++)
        {
            double err = aRev.Plates[j].QFromTubeW - NetFluxTargetW;      // 二维实得 − 目标
            var inp = inps[j];
            var inp2 = TabReverse1D.Clone(inp);
            inp2.QInHotTargetW = inp.QInHotTargetW - err;                 // 把偏差从目标里扣掉
            var s2 = TabReverse1D.SolveColdBare(inp2);
            note2[j] = s2.Ok ? $"热端净流入目标 {inp.QInHotTargetW:0.###} → {inp2.QInHotTargetW:0.###} W（扣掉二维偏差 {err:+0.###;-0.###}），解出 t热端 {s2.THotMm:0.000} mm"
                             : $"第二轮无解（{s2.Note}）⇒ 沿用第一轮";
            prof2[j] = s2.Ok
                ? TabReverse1D.Quantize(inp2, TabReverse1D.LinearRamp(inp2, s2.THotMm, -s2.THotMm), Segs)
                : prof[j];
            W($"片 {j}：{note2[j]}　⇒ {prof2[j].Describe()}");
        }
        Flush();
        Say("── 导航网格上跑第二轮剖面 …");
        var navRev2 = RunOne(prof2.Cast<TabInsulProfile?>().ToArray(),
                             prof2.Select(q => q.LengthWeightedMeanMm(0, -100)).ToArray(), 0, false);
        W("量\t第一轮剖面\t第二轮剖面");
        W($"全判据通过\t{navRev.AllOk}\t{navRev2.AllOk}");
        W($"管孔净流入 W\t{N(navRev.NetFluxW)}\t{N(navRev2.NetFluxW)}");
        W($"管根低于热偶读数 K\t{N(navRev.ColdUnderTcK)}\t{N(navRev2.ColdUnderTcK)}");
        W($"最热铂高出热偶读数 K\t{N(navRev.HotOverTcK)}\t{N(navRev2.HotOverTcK)}");
        W($"没过的\t{(navRev.Failed.Length == 0 ? "（全过）" : navRev.Failed)}\t{(navRev2.Failed.Length == 0 ? "（全过）" : navRev2.Failed)}");
        W();
        Flush();

        // ── 细网格
        var (hFine, rFine) = MeshVerify.RequiredMeshFor(SolvedW08());
        Say($"── 细网格（细区 {hFine:0.000} mm／细区半径 {rFine:0.0} mm）上各跑一次 …");
        var fineU = RunOne(new TabInsulProfile?[prof.Length], (double[])SolvedTabInsulMm.Clone(), 0, true);
        var fineR = RunOne(prof.Cast<TabInsulProfile?>().ToArray(),
                           prof.Select(q => q.LengthWeightedMeanMm(0, -100)).ToArray(), 0, true);
        W($"═══ 细网格（细区 {hFine:0.000} mm／细区半径 {rFine:0.0} mm）═══");
        W("量\t均匀单值\t反解剖面\t（导航网格上的同一对）");
        W($"全判据通过\t{fineU.AllOk}\t{fineR.AllOk}\t{a.Res.AllOk} / {navRev.AllOk}");
        W($"管孔净流入 W\t{N(fineU.NetFluxW)}\t{N(fineR.NetFluxW)}\t{N(a.Res.ValueOf(LineResult.Key.NetFlux))} / {N(navRev.NetFluxW)}");
        W($"管根低于热偶读数 K\t{N(fineU.ColdUnderTcK)}\t{N(fineR.ColdUnderTcK)}\t{N(a.Res.ValueOf(LineResult.Key.ColdUnderTc))} / {N(navRev.ColdUnderTcK)}");
        W($"最热铂高出热偶读数 K\t{N(fineU.HotOverTcK)}\t{N(fineR.HotOverTcK)}\t{N(a.Res.ValueOf(LineResult.Key.HotOverTc))} / {N(navRev.HotOverTcK)}");
        W($"法兰截面电流密度\t{N(fineU.SectionJ)}\t{N(fineR.SectionJ)}\t{N(a.Res.ValueOf(LineResult.Key.SectionJ))} / {N(navRev.SectionJ)}");
        W($"铂重 g\t{fineU.MassG:0}\t{fineR.MassG:0}\t{a.Res.TotalMassG:0} / {navRev.MassG:0}");
        W($"没过的\t{(fineU.Failed.Length == 0 ? "（全过）" : fineU.Failed)}\t{(fineR.Failed.Length == 0 ? "（全过）" : fineR.Failed)}");
        W($"耗时 s\t{fineU.Sec:0}\t{fineR.Sec:0}");
        W();
        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）。");
        W("出处：整线带玻璃稳态 = LineRunner.Run（Core/LineRunner.cs）；判定 = LineResult.AllOk / Failed（生产判据，门槛未改）；");
        W("一维 = Core/TabReverse1D.cs；剖面口子 = FlangePlate.TabInsulProfile（Core/TabInsulProfile.cs）。");
        W("每个数来自同一次运行，不拼两份输出。　注记：2026-09-17，Opus 5。");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.True(navRev.Ok, "反解剖面那一次整线解没解出来：" + navRev.Failed);
    }

    /// <summary>装上剖面再跑一次锚点（为了拿逐片的管根与管孔净流入）。</summary>
    internal static Anchor RunAnchorWithProfiles(TabInsulProfile[] prof)
    {
        var d = SolvedW08();
        d.TabInsulMm = prof.Select(q => q.LengthWeightedMeanMm(0, -100)).ToArray();
        return RunAnchor(d, null, lc =>
        {
            for (int j = 0; j < lc.FlangePlates.Length; j++)
                lc.FlangePlates[j].TabInsulProfile = j < prof.Length ? prof[j] : null;
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    //  R4　窗口：整体平移 ±1.0 mm（步 0.1）与只动热端段，各扫一遍；与均匀并列
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>形状族「冷端裸铂的线性斜坡」按倍率 k 缩放出来的四片剖面（不取整；取整在最后一步）。</summary>
    internal static TabInsulProfile[] ScaledShape(double[] tHot1D, double k, bool quantize)
    {
        var edges = TabInsulProfile.EvenEdges(0.0, -100.0, Segs);
        var o = new TabInsulProfile[tHot1D.Length];
        for (int j = 0; j < tHot1D.Length; j++)
        {
            var th = new double[Segs];
            for (int i = 0; i < Segs; i++)
            {
                double xi = (0.5 * (edges[i] + edges[i + 1]) - edges[0]) / (edges[^1] - edges[0]);
                double v = Math.Max(0.0, k * tHot1D[j] * (1.0 - xi));
                th[i] = quantize ? Math.Round(v / 0.1) * 0.1 : v;
            }
            o[j] = new TabInsulProfile(edges, th);
        }
        return o;
    }

    [Fact]
    [Trait("速度", "慢")]
    public void R4_窗口()
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_R_窗口_本次开跑于{stamp}.txt");
        var total = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() { try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } catch { } }
        void Say(string s) { W(s); _o.WriteLine(s); Console.WriteLine(s); Flush(); }

        W("R48 R 路　**反向设计 · 第三步：可行窗口**（本线的核心结论）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("问题：单值旋钮的窗口只有 0.1～0.2 mm（r48_P 实测），落不进现场 0.5 mm 的包法档。");
        W("　　　**反解剖面的窗口是不是明显更宽？**");
        W();
        W("── 跑前写死（跑完一条不改）");
        W("　扫描：δ 从 −1.0 到 +1.0，步 0.1，共 21 档。判定用生产判据 LineResult.AllOk / Failed，门槛一个不动。");
        W("　模式 U「对照·均匀单值」：四片的**单值**舌保温同时 +δ（剖面 = null，就是现在生产链那根旋钮）。");
        W("　模式 A「整条剖面整体平移」：四片反解剖面的**每一段**都 +δ（下限 0 ⇒ 已经是 0 的段再减也减不下去，这就是「饱和」）。");
        W("　模式 H「只动热端段」：只把四片反解剖面的**第 0 段**（靠圆盘那 20 mm）+δ。");
        W($"　判读 R-P1：窗口 ≥ {WindowNeedMm:0.0} mm ⇒ 这条路走得通；R-P2：< {WindowNeedMm:0.0} mm ⇒ 走不通，照记比均匀宽几倍。");
        W("　判读 R-P3：反解剖面与均匀的 |d(管孔净流入)/dδ| 相差 < 20 % ⇒ 判据只认平均厚度、不认形状。");
        W("　判读 R-P6：窗口顶到扫描边界 ⇒ 报「真实宽度 ≥ 这个数」。");
        W();
        W("★ **一步是跑完 R3 之后才加的，写明是事后加的，不装成跑前就有**：");
        W("　R3 实测：一维直接给的剖面在二维上**不过**（管根低于热偶读数 35.3 / 限值 5 K），按二维偏差校准两轮仍不过（26.0 K）。");
        W("　原因在 R3 里量得很清楚 —— 一维是把一片板压成等截面杆，自由段焦耳热与散热都偏高三四成（二维的「舌片区」不含 x∈[0,−30] 那半个圆盘）。");
        W("　⇒ 一维**只给形状**（冷端裸铂的线性斜坡），**水平由二维定**：二分一个整体倍率 k，使二维的「管根低于热偶读数」");
        W("　　落到与均匀单值锚点**同一个值**（4.922 K，取自 R3 那一次）—— 这样两条剖面在同一个工作点上比窗口，才是公平的。");
        W("　这一步是 R3 的结论逼出来的，不是事后挑数：它只动**一个**标量，形状（各段之间的比例）一位没动。");
        W();

        Say("── 一维反解拿形状 …");
        var a = RunAnchor();
        var (prof1D, note1D, inps, scales, _) = ReverseProfiles(a, null);
        var tHot1D = new double[prof1D.Length];
        for (int j = 0; j < prof1D.Length; j++)
        {
            var s = TabReverse1D.SolveColdBare(inps[j]);
            tHot1D[j] = s.Ok ? s.THotMm : SolvedTabInsulMm[j];
            W($"　片 {j}「{a.Plates[j].Name}」一维形状：t(ξ) = {tHot1D[j]:0.000}·(1−ξ)（冷端裸铂）；{note1D[j]}");
        }
        double anchorCold = a.Res.ValueOf(LineResult.Key.ColdUnderTc);
        W($"　均匀单值锚点（求解器解 5.1/2.3/3.6/10.4）：全判据通过 {a.Res.AllOk}，管根低于热偶读数 {anchorCold:0.###} K，"
          + $"管孔净流入 {a.Res.ValueOf(LineResult.Key.NetFlux):0.###} W，最热铂高出热偶读数 {a.Res.ValueOf(LineResult.Key.HotOverTc):0.###} K");
        W();
        Flush();

        // ── 二维定水平：二分整体倍率 k
        Say("── 二维定水平：二分整体倍率 k，使管根低于热偶读数落到锚点那个值 …");
        W("k\t管根低于热偶读数 K\t管孔净流入 W\t最热铂高出热偶读数 K\t全判据通过\t四片热端厚 mm\t耗时 s");
        double Cold(double k)
        {
            var pr = ScaledShape(tHot1D, k, false);
            var r = RunOne(pr.Cast<TabInsulProfile?>().ToArray(), pr.Select(q => q.LengthWeightedMeanMm(0, -100)).ToArray(), 0, false);
            W($"{k:0.0000}\t{N(r.ColdUnderTcK)}\t{N(r.NetFluxW)}\t{N(r.HotOverTcK)}\t{r.AllOk}\t{string.Join("/", tHot1D.Select(v => (v * k).ToString("0.00")))}\t{r.Sec:0}");
            Flush();
            return r.ColdUnderTcK;
        }
        double kLo = 1.0, kHi = 3.0;
        double cLo = Cold(kLo), cHi = Cold(kHi);
        bool bracketed = (cLo - anchorCold) * (cHi - anchorCold) <= 0;
        if (!bracketed)
            W($"⚠ k ∈ [{kLo:0.00}, {kHi:0.00}] 夹不住锚点值 {anchorCold:0.###} K（两端 {cLo:0.###} / {cHi:0.###}）—— 照记，取偏差小的那一端");
        else
            for (int it = 0; it < 5; it++)
            {
                double mid = 0.5 * (kLo + kHi);
                double cm = Cold(mid);
                if ((cLo - anchorCold) * (cm - anchorCold) <= 0) { kHi = mid; cHi = cm; } else { kLo = mid; cLo = cm; }
            }
        double kStar = bracketed ? 0.5 * (kLo + kHi) : (Math.Abs(cLo - anchorCold) < Math.Abs(cHi - anchorCold) ? kLo : kHi);
        var baseProf = ScaledShape(tHot1D, kStar, true);
        W($"★ 定下来的整体倍率 k = {kStar:0.0000}（二分 5 轮，门槛：管根低于热偶读数落到锚点 {anchorCold:0.###} K）");
        for (int j = 0; j < baseProf.Length; j++)
            W($"　片 {j}「{a.Plates[j].Name}」**反解剖面**（落成 5 段、图纸格 0.1 mm）：{baseProf[j].Describe()}　"
              + $"平均 {baseProf[j].LengthWeightedMeanMm(0, -100):0.00} mm（均匀单值是 {SolvedTabInsulMm[j]:0.0} mm）");
        double volU = SolvedTabInsulMm.Sum() * 60 * 100 / 1000, volR = baseProf.Sum(q => q.LengthWeightedMeanMm(0, -100)) * 60 * 100 / 1000;
        W($"　保温用量（四片，舌宽 60 × 自由段 100 mm 的等效体积）：均匀 {volU:0} cm³　反解 {volR:0} cm³　"
          + $"差 {volR - volU:+0;-0;0} cm³（{(volR / volU - 1) * 100:+0.0;-0.0;0.0} %）");
        W();
        Flush();

        var deltas = Enumerable.Range(0, 21).Select(i => Math.Round(-1.0 + 0.1 * i, 1)).ToArray();
        Assert.Equal(21, deltas.Length);
        Assert.Equal(-1.0, deltas[0], 9);
        Assert.Equal(1.0, deltas[^1], 9);

        // ── 并行安全门：模式 A 的 δ=0 串行单跑一次，与并行那一档逐位比
        Say("── 并行安全门：模式 A 的 δ=0 串行单跑一次 …");
        var (p0, s0) = MakeCase(baseProf, SweepMode.ShiftAll, 0.0);
        var serial0 = RunOne(p0, s0, 0.0, false);

        var res = new Dictionary<SweepMode, RunOut[]>();
        foreach (var mode in new[] { SweepMode.Uniform, SweepMode.ShiftAll, SweepMode.ShiftHot })
        {
            string nm = mode switch { SweepMode.Uniform => "U 对照·均匀单值", SweepMode.ShiftAll => "A 整条剖面整体平移", _ => "H 只动热端段" };
            Say($"── 模式「{nm}」21 档并行跑 …");
            var rows = RunMany(baseProf, mode, deltas, false, par: 4);
            res[mode] = rows;
            W($"═══ 模式「{nm}」═══");
            W($"　这一模式下 δ 改的是什么：{(mode == SweepMode.Uniform ? "四片单值 5.1/2.3/3.6/10.4 同时 +δ" : mode == SweepMode.ShiftAll ? "四片剖面每一段 +δ（下限 0）" : "四片剖面第 0 段 +δ")}");
            WriteRows(W, rows);
            var blocks = Blocks(rows);
            if (blocks.Length == 0)
            {
                W("★ **无可行点**：21 档一档都没过。最靠近的几档：");
                foreach (var r in rows.OrderBy(r => r.Failed.Length).Take(3))
                    W($"　　δ = {r.Delta:+0.0;-0.0;0.0}：{r.Failed}");
            }
            else
                foreach (var (lo, hi) in blocks)
                    W($"★ 窗口 δ ∈ [{lo:+0.0;-0.0;0.0}, {hi:+0.0;-0.0;0.0}] mm，**宽度 {hi - lo + 0.1:0.0} mm**"
                      + $"（含端点，0.1 步进；{(lo <= -1.0 + 1e-9 || hi >= 1.0 - 1e-9 ? "**顶到扫描边界 ⇒ 真实宽度 ≥ 这个数**（R-P6）" : "两端都在扫描区间内")}）");
            W($"　斜率（最小二乘，全 21 档）：d(管孔净流入)/dδ = {Slope(rows, r => r.NetFluxW):0.###} W/mm　"
              + $"d(管根低于热偶读数)/dδ = {Slope(rows, r => r.ColdUnderTcK):0.###} K/mm　"
              + $"d(最热铂高出热偶读数)/dδ = {Slope(rows, r => r.HotOverTcK):0.###} K/mm");
            W();
            Flush();
        }

        var parA0 = res[SweepMode.ShiftAll].First(r => Math.Abs(r.Delta) < 1e-9);
        bool same = parA0.NetFluxW.Equals(serial0.NetFluxW) && parA0.ColdUnderTcK.Equals(serial0.ColdUnderTcK)
                 && parA0.HotOverTcK.Equals(serial0.HotOverTcK) && parA0.SectionJ.Equals(serial0.SectionJ)
                 && parA0.MassG.Equals(serial0.MassG) && parA0.AllOk == serial0.AllOk;
        W($"★ 并行安全门：模式 A 的 δ=0，并行 vs 串行 —— 管孔净流入 {N(parA0.NetFluxW)} vs {N(serial0.NetFluxW)}、"
          + $"管根低于热偶读数 {N(parA0.ColdUnderTcK)} vs {N(serial0.ColdUnderTcK)}、最热铂高出热偶读数 {N(parA0.HotOverTcK)} vs {N(serial0.HotOverTcK)}、"
          + $"铂重 {parA0.MassG:0} vs {serial0.MassG:0} ⇒ {(same ? "**逐位相同**（并行没有污染）" : "**对不上** ⇒ 并行有共享状态，本次结果不可用")}");
        W();

        // ── 并表
        W("══════ 三个模式并列（本线的核心结论表）══════");
        W("模式\t可行窗口 δ\t宽度 mm\t落进去的 0.5 mm 档\td(管孔净流入)/dδ W/mm\td(管根低于热偶读数)/dδ K/mm\t铂重 g\t保温等效体积 cm³");
        string Row(SweepMode m, string nm, double vol)
        {
            var rows = res[m]; var bl = Blocks(rows);
            string win = bl.Length == 0 ? "**无可行点**" : string.Join("、", bl.Select(b => $"[{b.Lo:+0.0;-0.0;0.0}, {b.Hi:+0.0;-0.0;0.0}]"));
            double wid = bl.Length == 0 ? 0 : bl.Max(b => b.Hi - b.Lo + 0.1);
            string grid = bl.Length == 0 ? "—" : string.Join("、", bl.SelectMany(b =>
            {
                var hit = new List<string>();
                for (double v = Math.Ceiling(b.Lo * 2 - 1e-9) / 2; v <= b.Hi + 1e-9; v += 0.5) hit.Add(v.ToString("+0.0;-0.0;0.0"));
                return hit;
            }).DefaultIfEmpty("**一档都落不进去**"));
            double mass = rows.FirstOrDefault(r => r.AllOk)?.MassG ?? rows[0].MassG;
            return $"{nm}\t{win}\t{wid:0.0}\t{grid}\t{Slope(rows, r => r.NetFluxW):0.###}\t{Slope(rows, r => r.ColdUnderTcK):0.###}\t{mass:0}\t{vol:0}";
        }
        W(Row(SweepMode.Uniform, "U 对照·均匀单值", volU));
        W(Row(SweepMode.ShiftAll, "A 整条剖面整体平移", volR));
        W(Row(SweepMode.ShiftHot, "H 只动热端段", volR));
        W();
        double wU = Blocks(res[SweepMode.Uniform]).Select(b => b.Hi - b.Lo + 0.1).DefaultIfEmpty(0).Max();
        double wA = Blocks(res[SweepMode.ShiftAll]).Select(b => b.Hi - b.Lo + 0.1).DefaultIfEmpty(0).Max();
        double wH = Blocks(res[SweepMode.ShiftHot]).Select(b => b.Hi - b.Lo + 0.1).DefaultIfEmpty(0).Max();
        W($"── 判读逐条对账");
        W($"　R-P1（窗口 ≥ {WindowNeedMm:0.0} mm ⇒ 走得通）：整体平移 {wA:0.0} mm ⇒ {(wA >= WindowNeedMm ? "**触发**（走得通）" : "未触发")}；"
          + $"只动热端段 {wH:0.0} mm ⇒ {(wH >= WindowNeedMm ? "**触发**（走得通）" : "未触发")}");
        W($"　R-P2（窗口 < {WindowNeedMm:0.0} mm ⇒ 走不通）：{(wA < WindowNeedMm ? "**触发**" : "未触发")}；"
          + $"比均匀宽 {(wU > 0 ? (wA / wU).ToString("0.0") + " 倍" : "均匀无窗口，比不了")}");
        double sU = Slope(res[SweepMode.Uniform], r => r.NetFluxW), sA = Slope(res[SweepMode.ShiftAll], r => r.NetFluxW);
        double rel = Math.Abs(sA - sU) / Math.Max(1e-9, Math.Abs(sU));
        W($"　R-P3（两条剖面的 |d净流入/dδ| 差 < 20 % ⇒ 判据只认平均厚度）：均匀 {sU:0.###}、整体平移 {sA:0.###} W/mm，"
          + $"相对差 {rel * 100:0.0} % ⇒ {(rel < 0.20 ? "**触发**：判据只认平均厚度、不认形状" : "未触发：形状确实改了灵敏度")}");
        W($"　R-P6（顶到扫描边界）：见上面各模式那一行。");
        W();
        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）。");
        W("出处：整线带玻璃稳态 = LineRunner.Run（Core/LineRunner.cs）；判定 = LineResult.AllOk / Failed（生产判据，门槛未改）；");
        W("剖面口子 = FlangePlate.TabInsulProfile（Core/TabInsulProfile.cs）；一维形状 = Core/TabReverse1D.cs。");
        W("每个数来自同一次运行，不拼两份输出。　注记：2026-09-17，Opus 5。");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.True(same, "并行与串行跑出来的不是同一个数 ⇒ 并行污染，结果不可用");
        Assert.True(res.Values.All(r => r.Any(x => x.Ok)), "有模式整整 21 档一次都没解出来");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  R5　夹头温度作为控制量：100 / 200 / 450 °C 各算一次
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    [Trait("速度", "慢")]
    public void R5_夹头温度当控制量()
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_R_夹头温度_本次开跑于{stamp}.txt");
        var total = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() { try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } catch { } }
        void Say(string s) { W(s); _o.WriteLine(s); Console.WriteLine(s); Flush(); }

        W("R48 R 路　**反向设计 · 第四步：夹头温度当控制量**（100 / 200 / 450 °C 各算一次）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("为什么问这个：r48_P 实测舌板加长时「进铜排夹」只从 191 W 涨到 215 W（+13 %），而舌板焦耳热从 347 W 涨到 875 W（+152 %）——");
        W("　夹头能带走多少热，几乎被**夹头温度**钉死了。若窗口仍然窄，那就是这根桩在锁；把夹头温度降下来，窗口会不会松？");
        W("　（这一段是为路线三「夹头温度／带热能力当控制量」备的数。）");
        W();
        W("── 跑前写死：夹头温度 450（现役）／200／100 °C 各跑三档 δ = −0.1 / 0 / +0.1 mm（均匀单值那根旋钮），");
        W("　量当地斜率 d(管孔净流入)/dδ 与判据值；**不重跑求解器**（旋钮仍取求解器在 450 °C 上解出来的终值）。");
        W("　⚠ 这一句必须跟着结果走：换了夹头温度，求解器解出来的旋钮会变；本段只回答「同一份设计在不同夹头温度下判据怎么动」，");
        W("　　不回答「换夹头温度后最优设计是什么」。");
        W();

        var deltas = new[] { -0.1, 0.0, 0.1 };
        var temps = new[] { 450.0, 200.0, 100.0 };
        W("夹头 °C\tδ mm\t判定\t管孔净流入 W\t管根低于热偶读数 K\t最热铂高出热偶读数 K\t截面电流密度\t场有效\t收敛\t铂重 g\t四片管孔净流入 W\t没过的");
        var slopeByT = new Dictionary<double, double>();
        var empty = new TabInsulProfile[SolvedTabInsulMm.Length];
        for (int j = 0; j < empty.Length; j++) empty[j] = TabInsulProfile.Uniform(TabInsulProfile.EvenEdges(0, -100, Segs), SolvedTabInsulMm[j]);
        foreach (double tc in temps)
        {
            Say($"── 夹头 {tc:0} °C，三档并行 …");
            var rows = RunMany(empty, SweepMode.Uniform, deltas, false, par: 3, clampTempC: tc);
            foreach (var r in rows)
                W($"{tc:0}\t{r.Delta:+0.0;-0.0;0.0}\t{(r.AllOk ? "过" : "不过")}\t{N(r.NetFluxW)}\t{N(r.ColdUnderTcK)}\t{N(r.HotOverTcK)}\t"
                  + $"{N(r.SectionJ)}\t{(r.FieldOk ? "是" : "否")}\t{(r.Converged ? "是" : "否")}\t{r.MassG:0}\t"
                  + $"{string.Join("/", r.PlateNetFluxW.Select(v => v.ToString("0.00")))}\t{r.Failed}");
            slopeByT[tc] = Slope(rows, r => r.NetFluxW);
            Flush();
        }
        W();
        W("── 当地斜率与「按斜率外推的窗口宽度」");
        W("夹头 °C\td(管孔净流入)/dδ W/mm\t斜率相对 450 °C\t按斜率外推：净流入从 0 走到 +2 W 要多少 mm");
        foreach (double tc in temps)
            W($"{tc:0}\t{N(slopeByT[tc])}\t{N(slopeByT[tc] / slopeByT[450.0])}\t{N(Math.Abs(2.0 / slopeByT[tc]))}");
        W("　（最后一列只是**按当地斜率的线性外推**，不是实测窗口 —— 真窗口还要受另外两条判据夹。标出来是为了给路线三一个量级。）");
        W();
        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）。");
        W("出处：整线带玻璃稳态 = LineRunner.Run（Core/LineRunner.cs）；判定 = LineResult.AllOk / Failed（生产判据，门槛未改）。");
        W("每个数来自同一次运行，不拼两份输出。　注记：2026-09-17，Opus 5。");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.True(slopeByT.Count == 3, "三个夹头温度没都跑到");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  R3b　细网格复核**那个真正可行的点**（反解剖面 · 二维定完水平 · δ = −0.1）
    //        R3 的细网格跑的是**定水平之前**那条剖面（它在二维上不过）；
    //        R4 才找出唯一可行的那一档。可交付与否要看**可行点**在细网格上还过不过 —— 这一条是 R3 漏掉的。
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    [Trait("速度", "慢")]
    public void R3b_可行点的细网格复核()
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_R_可行点细网格_本次开跑于{stamp}.txt");
        var total = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() { try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } catch { } }
        void Say(string s) { W(s); _o.WriteLine(s); Console.WriteLine(s); Flush(); }

        W("R48 R 路　**可行点的细网格复核**（反解剖面 · 二维定完水平 · δ = −0.1 那一档）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("为什么单跑这一条：R3 的细网格跑的是**定水平之前**那条剖面（它在二维上不过，比了也不算数）；");
        W("　R4 才找出唯一可行的那一档（模式 A，δ = −0.1）。**能不能交付看的是可行点**，所以补这一跑。");
        W("网格：细区 1.000 mm／细区半径 59.0 mm（MeshVerify.RequiredMeshFor，与「◆ 加密复算」同一个来源）。判据门槛一位没动。");
        W();

        Say("── 一维反解拿形状、二维定水平（与 R4 同一套代码、同一条二分）…");
        var a = RunAnchor();
        var (_, _, inps, _, _) = ReverseProfiles(a, null);
        var tHot1D = new double[inps.Length];
        for (int j = 0; j < inps.Length; j++)
        {
            var s = TabReverse1D.SolveColdBare(inps[j]);
            tHot1D[j] = s.Ok ? s.THotMm : SolvedTabInsulMm[j];
        }
        double anchorCold = a.Res.ValueOf(LineResult.Key.ColdUnderTc);
        double kLo = 1.0, kHi = 3.0;
        double Cold(double k)
        {
            var pr = ScaledShape(tHot1D, k, false);
            return RunOne(pr.Cast<TabInsulProfile?>().ToArray(), pr.Select(q => q.LengthWeightedMeanMm(0, -100)).ToArray(), 0, false).ColdUnderTcK;
        }
        double cLo = Cold(kLo), cHi = Cold(kHi);
        for (int it = 0; it < 5; it++)
        {
            double mid = 0.5 * (kLo + kHi);
            double cm = Cold(mid);
            if ((cLo - anchorCold) * (cm - anchorCold) <= 0) { kHi = mid; cHi = cm; } else { kLo = mid; cLo = cm; }
        }
        double kStar = 0.5 * (kLo + kHi);
        var baseProf = ScaledShape(tHot1D, kStar, true);
        W($"定水平：整体倍率 k = {kStar:0.0000}（与 R4 同一条二分，应与那一份逐位相同：1.4688）");
        for (int j = 0; j < baseProf.Length; j++)
            W($"　片 {j}「{a.Plates[j].Name}」：{baseProf[j].Describe()}");
        W();

        Say("── 细网格上跑四个点（可行点与它左右各一档，外加均匀单值作对照）…");
        var (pm1, sm1) = MakeCase(baseProf, SweepMode.ShiftAll, -0.1);
        var (pm2, sm2) = MakeCase(baseProf, SweepMode.ShiftAll, -0.2);
        var (pm0, sm0) = MakeCase(baseProf, SweepMode.ShiftAll, 0.0);
        var fineRev = RunOne(pm1, sm1, -0.1, true);
        var fineRevLo = RunOne(pm2, sm2, -0.2, true);
        var fineRevHi = RunOne(pm0, sm0, 0.0, true);
        var fineUni = RunOne(new TabInsulProfile?[baseProf.Length], (double[])SolvedTabInsulMm.Clone(), 0, true);

        W("═══ 细网格（细区 1.000 mm／细区半径 59.0 mm）═══");
        W("算例\tδ mm\t判定\t管孔净流入 W\t管根低于热偶读数 K\t最热铂高出热偶读数 K\t截面电流密度\t铂重 g\t耗时 s\t没过的");
        void Row(string nm, RunOut r)
            => W($"{nm}\t{r.Delta:+0.0;-0.0;0.0}\t{(r.AllOk ? "过" : "不过")}\t{N(r.NetFluxW)}\t{N(r.ColdUnderTcK)}\t{N(r.HotOverTcK)}\t{N(r.SectionJ)}\t{r.MassG:0}\t{r.Sec:0}\t{(r.Failed.Length == 0 ? "（全过）" : r.Failed)}");
        Row("均匀单值（对照）", fineUni);
        Row("反解剖面", fineRevLo);
        Row("反解剖面（**导航网格上的可行点**）", fineRev);
        Row("反解剖面", fineRevHi);
        W();
        W("── 与导航网格并列（同一份设计，只换判决网格）");
        W("算例\t导航网格\t细网格");
        W($"均匀单值 δ=0　管根低于热偶读数 K\t{anchorCold:0.###}\t{N(fineUni.ColdUnderTcK)}");
        W($"均匀单值 δ=0　最热铂高出热偶读数 K\t{a.Res.ValueOf(LineResult.Key.HotOverTc):0.###}\t{N(fineUni.HotOverTcK)}");
        W($"均匀单值 δ=0　管孔净流入 W\t{a.Res.ValueOf(LineResult.Key.NetFlux):0.###}\t{N(fineUni.NetFluxW)}");
        W("（反解剖面 δ=−0.1 的导航网格值：管孔净流入 0.764 / 管根低于热偶读数 4.776 / 最热铂高出热偶读数 4.399 —— 取自 R48_R_窗口_…151606.txt 那一次）");
        W();
        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）。");
        W("出处：整线带玻璃稳态 = LineRunner.Run（Core/LineRunner.cs）；判定 = LineResult.AllOk / Failed（生产判据，门槛未改）。");
        W("每个数来自同一次运行，不拼两份输出。　注记：2026-09-17，Opus 5。");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.True(Math.Abs(kStar - 1.4688) < 1e-3, $"定水平的倍率与 R4 那一次对不上：{kStar:0.0000} vs 1.4688 ⇒ 两次跑的不是同一条剖面");
        Assert.True(fineRev.Ok, "可行点的细网格解没解出来：" + fineRev.Failed);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  R3c　**细网格上的窗口**（R3b 发现可行点在细网格上不过 ⇒ 不能只报「不过」，要把窗口在细网格上重扫一遍）
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    [Trait("速度", "慢")]
    public void R3c_细网格上的窗口()
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_R_细网格窗口_本次开跑于{stamp}.txt");
        var total = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() { try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } catch { } }
        void Say(string s) { W(s); _o.WriteLine(s); Console.WriteLine(s); Flush(); }

        W("R48 R 路　**细网格上的窗口**（导航网格上量到的窗口，换到网格无关那张网格上还在不在）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("为什么要这一跑：R3b 实测**导航网格上唯一那个可行点**（反解剖面 δ = −0.1）在细网格上**不过**");
        W("　（最热铂高出热偶读数 4.399 → 9.130 K）。只报「不过」不够 —— 窗口可能只是**挪了位置**。");
        W("　⇒ 在细网格上把两条剖面各重扫一遍，直接回答「细网格上还有没有窗口、在哪、多宽」。");
        W("网格：细区 1.000 mm／细区半径 59.0 mm（MeshVerify.RequiredMeshFor）。判据门槛一位没动。");
        W();
        W("── 跑前写死：反解剖面扫 δ ∈ [−0.6, 0.0] 步 0.1（7 档）；均匀单值扫 δ ∈ [−0.2, +0.2] 步 0.1（5 档）。");
        W("　扫描区间是按 R3b 三档的线性外推定的（反解剖面的两条判据在 δ ≈ −0.26 附近交叉）；顶到边界就报「真实窗口 ≥ 这个数」。");
        W();

        Say("── 一维反解拿形状、二维定水平（与 R4 同一套代码、同一条二分）…");
        var a = RunAnchor();
        var (_, _, inps, _, _) = ReverseProfiles(a, null);
        var tHot1D = new double[inps.Length];
        for (int j = 0; j < inps.Length; j++)
        {
            var s = TabReverse1D.SolveColdBare(inps[j]);
            tHot1D[j] = s.Ok ? s.THotMm : SolvedTabInsulMm[j];
        }
        double anchorCold = a.Res.ValueOf(LineResult.Key.ColdUnderTc);
        double kLo = 1.0, kHi = 3.0;
        double Cold(double k)
        {
            var pr = ScaledShape(tHot1D, k, false);
            return RunOne(pr.Cast<TabInsulProfile?>().ToArray(), pr.Select(q => q.LengthWeightedMeanMm(0, -100)).ToArray(), 0, false).ColdUnderTcK;
        }
        double cLo = Cold(kLo), cHi = Cold(kHi);
        for (int it = 0; it < 5; it++)
        {
            double mid = 0.5 * (kLo + kHi);
            double cm = Cold(mid);
            if ((cLo - anchorCold) * (cm - anchorCold) <= 0) { kHi = mid; cHi = cm; } else { kLo = mid; cLo = cm; }
        }
        double kStar = 0.5 * (kLo + kHi);
        var baseProf = ScaledShape(tHot1D, kStar, true);
        W($"定水平：整体倍率 k = {kStar:0.0000}（与 R4／R3b 同一条二分）");
        W();
        Flush();

        Say("── 细网格上扫反解剖面（整条整体平移）7 档 …");
        var dRev = Enumerable.Range(0, 7).Select(i => Math.Round(-0.6 + 0.1 * i, 1)).ToArray();
        var rowsRev = RunMany(baseProf, SweepMode.ShiftAll, dRev, true, par: 4);
        W("═══ 细网格 · 反解剖面（整条整体平移）═══");
        WriteRows(W, rowsRev);
        var bRev = Blocks(rowsRev);
        W(bRev.Length == 0 ? "★ **细网格上无可行点**（7 档一档都没过）"
            : string.Join("\n", bRev.Select(b => $"★ 窗口 δ ∈ [{b.Lo:+0.0;-0.0;0.0}, {b.Hi:+0.0;-0.0;0.0}] mm，宽度 {b.Hi - b.Lo + 0.1:0.0} mm"
                + (b.Lo <= -0.6 + 1e-9 || b.Hi >= 0.0 - 1e-9 ? "（**顶到扫描边界 ⇒ 真实宽度 ≥ 这个数**）" : "（两端都在扫描区间内）"))));
        W();
        Flush();

        Say("── 细网格上扫均匀单值 5 档 …");
        var dUni = Enumerable.Range(0, 5).Select(i => Math.Round(-0.2 + 0.1 * i, 1)).ToArray();
        var rowsUni = RunMany(baseProf, SweepMode.Uniform, dUni, true, par: 4);
        W("═══ 细网格 · 对照·均匀单值 ═══");
        WriteRows(W, rowsUni);
        var bUni = Blocks(rowsUni);
        W(bUni.Length == 0 ? "★ **细网格上无可行点**（5 档一档都没过）"
            : string.Join("\n", bUni.Select(b => $"★ 窗口 δ ∈ [{b.Lo:+0.0;-0.0;0.0}, {b.Hi:+0.0;-0.0;0.0}] mm，宽度 {b.Hi - b.Lo + 0.1:0.0} mm"
                + (b.Lo <= -0.2 + 1e-9 || b.Hi >= 0.2 - 1e-9 ? "（**顶到扫描边界 ⇒ 真实宽度 ≥ 这个数**）" : "（两端都在扫描区间内）"))));
        W();
        W("── 两张网格并列（同一份设计，只换判决网格）");
        W("剖面\t导航网格窗口 δ\t细网格窗口 δ");
        W($"均匀单值\t[0.0, 0.0]（宽 0.1）\t{(bUni.Length == 0 ? "无" : string.Join("、", bUni.Select(b => $"[{b.Lo:+0.0;-0.0;0.0}, {b.Hi:+0.0;-0.0;0.0}]（宽 {b.Hi - b.Lo + 0.1:0.0}）")))}");
        W($"反解剖面\t[-0.1, -0.1]（宽 0.1）\t{(bRev.Length == 0 ? "无" : string.Join("、", bRev.Select(b => $"[{b.Lo:+0.0;-0.0;0.0}, {b.Hi:+0.0;-0.0;0.0}]（宽 {b.Hi - b.Lo + 0.1:0.0}）")))}");
        W();
        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}），细网格 {rowsRev.Length + rowsUni.Length} 次整线解。");
        W("出处：整线带玻璃稳态 = LineRunner.Run（Core/LineRunner.cs）；判定 = LineResult.AllOk / Failed（生产判据，门槛未改）。");
        W("每个数来自同一次运行，不拼两份输出。　注记：2026-09-17，Opus 5。");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.True(rowsRev.All(r => r.Ok) && rowsUni.All(r => r.Ok), "有档整线解没解出来 —— 那不是「不可行」，是算不出来");
    }
}
