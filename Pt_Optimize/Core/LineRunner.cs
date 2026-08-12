using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// 整线算例的**全部输入**。管子走数值，法兰形状走 Rhino（每片可用不同 .3dm）。
/// </summary>
public sealed class LineCase
{
    // ── 管（数值输入）
    public double TubeIdMm = 50.0;
    public double WallMm = 1.0;
    public double SegLengthMm = 300.0;
    public string GradeName = "Pt";

    /// <summary>各段控温点 °C（控温点在每段中点）。长度即段数。</summary>
    public double[] SetpointC = { 1150, 1080, 1050 };
    /// <summary>各段玻璃压力水头 m</summary>
    public double[] HeadM = { 0.3, 0.6, 1.0 };

    // ── 电流：两种模式
    /// <summary>true = 用实测电流（秒级）；false = 由控温点反算（分钟级，可预测新几何）</summary>
    public bool UseMeasuredCurrent = true;

    /// <summary>
    /// ⚠ **这三个数不是实测值** —— 是模型自己在实况保温下反算出来的（§4.2g：1663/1519/1466，
    /// §4.2l：1655）。现场至今**没有给过电流实测**（§6 待补：稳态实测电流）。
    ///
    /// 保留字段名是为了不破坏算例文件，但读结果时必须记得：用它跑出来的
    /// 「实测电流模式」其实是「模型电流模式」，两边同源，**不构成任何验证**。
    /// 用户只给过定性判断「稳态电流密度远低于 10 A/mm²」，而这组数对应 J≈10.3 ——
    /// 即它们本身就被认为偏大（§4.2l）。真拿到实测值请直接覆盖本数组。
    /// （§7 的教训「示例参数被当成实测」，这里差点又犯一次。）
    /// </summary>
    public double[] MeasuredCurrentA = { 1655, 1510, 1446 };

    /// <summary>
    /// **解析几何**的法兰（长度 = 段数+1）。非空时**优先于** <see cref="FlangeFile3dm"/>。
    ///
    /// 用途：参数化搜索阶段用它遍历形状（圆盘半径、舌片长宽、阶梯厚度分布），
    /// 定下来之后再由 Rhino 出 .3dm 走 <see cref="FlangeFile3dm"/> 复核。
    /// 两条路进的是**同一个** ShellMesh → ShellCurrent → ShellThermal，
    /// 差别只在厚度场是解析给的还是量出来的（§4.2j 已做过两者的回归）。
    /// </summary>
    public FlangePlate[] FlangePlates = Array.Empty<FlangePlate>();

    // ── 法兰（每片一个 .3dm，长度 = 段数+1；可重复同一文件）
    public string[] FlangeFile3dm = Array.Empty<string>();
    public string FlangeLayer = "法兰";
    /// <summary>每片在 .3dm 中的平面 Y；NaN = 取该图层第一片</summary>
    public double[] FlangePlaneY = Array.Empty<double>();
    /// <summary>厚度场提取步长 mm（1.0 足够分辨槽与阶梯）</summary>
    public double ThicknessStepMm = 1.0;

    /// <summary>
    /// 每片的**厚度整体标度**（长度 = 片数，缺省全 1）。仅 .3dm 路径生效。
    ///
    /// 为什么需要它：.3dm 给的是**固定**厚度，而 C2（管根温差 &lt;10 K）要求
    /// 法兰厚度精确到 ±0.008 mm（ΔT 对厚度斜率约 1300 K/mm，§4.2w）——
    /// 不可能靠画图碰运气碰到。于是把厚度整体缩放当成自由度：
    /// 自动定厚求出的是「这张图纸的厚度要整体 ×k」，工程师照 k 改一版图即可。
    ///
    /// t=0 的格（轮廓外、管孔、开槽）乘任何数仍是 0，**槽与轮廓不受影响**。
    /// </summary>
    public double[] ThicknessScale = Array.Empty<double>();

    // ── 网格
    public double MeshFineMm = 2.0, MeshCoarseMm = 11.0, MeshFineRadiusMm = 50.0;

    // ── 玻璃与验证
    public double GlassInC = 1150, GlassOutMeasuredC = 1130;

    // ── 判据（HANDOVER §4.2k 的判据体系）
    /// <summary>规程一：空管升温。关掉可省几秒，但那是**决定最小截面**的那条，默认开。</summary>
    public bool CheckRamp = true;
    public double RampFromC = 25, RampTargetC = 1150, RampHours = 3.0;
    /// <summary>管根温差目标上限 K（③）。下限恒为 0：温差必须为正，即法兰比管冷。</summary>
    public double RootDeltaMaxK = 10.0;

    // ── 段↔法兰外层耦合的数值参数（见 LineRunner.Run 里为什么必须欠松弛）
    /// <summary>欠松弛因子。1.0 = 裸 Picard，在法兰倒灌的正反馈下会发散。</summary>
    public double CoupleRelax = 0.35;
    public int CoupleMaxRounds = 15;
    /// <summary>收敛判据：相邻两轮管根温度变化 K</summary>
    public double CoupleTolK = 1.0;

    /// <summary>其余物性、保温、电气、环境沿用 DesignInputs</summary>
    public DesignInputs Base = new();

    /// <summary>现状整线铂重基准 g（--geom 校核值），用于算省铂率</summary>
    public double BaselineMassG = 7141.0;

    public int SegmentCount => SetpointC.Length;
    public int FlangeCount => LineSolver.FlangeCount(SegmentCount);
}

public sealed class SegmentOut
{
    public string Name = "";
    public double SetpointC, CurrentA, PowerW, TubeJAPerMm2;
    public double TRootC, RootDeltaK, GlassInC, GlassOutC, MassG;
    public double[] X = Array.Empty<double>();
    public double[] TMetal = Array.Empty<double>();
    public double[] TGlass = Array.Empty<double>();
}

public sealed class FlangeOut
{
    public string Name = "";
    public bool Shared;
    public double CurrentA, MassG, JMaxAPerMm2, Phi, QFromTubeW, TMaxC, TMinC, TTabEndC;
    /// <summary>自身焦耳热与自身散热 W —— Φ = QGen/QLoss 的两个分子分母，判 §4.2k 时要看得见</summary>
    public double QGenW, QLossW;
    /// <summary>本片贴着的管根温度 °C（管孔定温边界）。TMaxC − TRootC &gt; 0 即「法兰比管热」</summary>
    public double TRootC;
    public double AreaMm2, VolumeMm3;
    public int CellCount;
    public ShellMesh? Mesh;
    public double[] JField = Array.Empty<double>();
    public double[] TField = Array.Empty<double>();
    public string Source = "";        // 用了哪个 .3dm
}

/// <summary>
/// 判据的**分级**。HANDOVER §4.2k 之后三者不再等价：
/// 硬安全线越了就是烧断，目标越了只是不够好，参考量根本不参与判定。
/// 早先把三者混在一张表里，导致「J 越限」与「Φ&gt;1」被同等对待 —— 前者可能只是限值本身存疑
/// （§4.2i：J_allow=10 的物理依据未定），后者是确凿的失效模式。
/// </summary>
public enum CheckKind
{
    /// <summary>硬安全线：越界即失效，没有折衷余地</summary>
    HardSafety,
    /// <summary>设计目标：越界是方案不够好，可与省铂权衡</summary>
    Target,
    /// <summary>参考量：只报数，Ok 恒为 true</summary>
    Reference
}

public sealed class ConstraintOut
{
    public string Name = "", Where = "", Unit = "", Note = "";
    public double Actual, Limit;
    public bool Ok;
    /// <summary>true = 实际值须 ≤ 限值；false = 须 ≥ 限值</summary>
    public bool LessIsBetter = true;
    public CheckKind Kind = CheckKind.Target;
    /// <summary>数据不足以判定（如纯铂在 1100 °C 以下无持久强度实测，见 §6 待补 ③）</summary>
    public bool Undetermined;
}

public sealed class LineResult
{
    public SegmentOut[] Segments = Array.Empty<SegmentOut>();
    public FlangeOut[] Flanges = Array.Empty<FlangeOut>();
    public ConstraintOut[] Checks = Array.Empty<ConstraintOut>();
    public double TubeMassG, FlangeMassG, TotalMassG, BaselineMassG, SavingPct;
    public double GlassDropModelK, GlassDropMeasuredK;
    public readonly List<string> Notes = new();
    public bool Ok = true;
    /// <summary>段↔法兰外层耦合是否收敛。**为 false 时表内所有数值一律不可引用。**</summary>
    public bool Converged;
    public string Message = "";
}

/// <summary>
/// **整线求解的唯一入口** —— CLI 与 WinForms 都只调 <see cref="Run"/>，
/// 于是两边不可能跑出不同结果（此前 CLI 与 UI 各自拼装流程，是长期的不一致来源）。
///
/// 流程：
///   ① 逐段定电流：实测模式直接取；反算模式用 SegmentSolver 二分使中点达设定温度
///   ② 逐段解管温：得管根温度、轴向剖面、玻璃出口温度（串联到下一段）
///   ③ 逐片解法兰：厚度场(Rhino, 带缓存) → 变步长壳网格 → 电流场 → 温度场
///      共用片电流用 §4.2h 的 √(I₁²+I₂²+I₁I₂)
///   ④ 汇总质量、判定约束
/// </summary>
public static class LineRunner
{
    public static LineResult Run(LineCase c, IProgress<string>? progress = null,
                                 CancellationToken cancel = default)
    {
        var res = RunOnce(c, progress, cancel, null);
        if (!res.Ok) return res;

        // ── 外层耦合：段 ↔ 法兰。首轮段解用抽热 0，拿到壳温度场后回灌重解。
        //
        // ★ 必须**欠松弛**。这个不动点自带正反馈：法兰热 ⇒ 向管根倒灌 ⇒ 管根更热 ⇒
        //   法兰边界温度更高 ⇒ 法兰更热。裸 Picard（ω=1）在该反馈下发散，
        //   现役几何上实测三轮后管根温差还有 204 K，输出的每个数都不可信 ——
        //   而那正是曾被读成「模型判现役设备烧断」的那批数。
        //   欠松弛不改变不动点，只改变到达方式：**若加了松弛仍发散，那才是物理上的热失控**。
        double omega = c.CoupleRelax;
        double[]? draws = null;
        double delta = double.NaN;
        for (int outer = 0; outer < c.CoupleMaxRounds; outer++)
        {
            cancel.ThrowIfCancellationRequested();
            var target = new double[c.SegmentCount];
            for (int i = 0; i < c.SegmentCount; i++)
            {
                // 段 i 的两端分别是法兰 i 与 i+1，各贡献自己的抽热
                double a = res.Flanges[i].QFromTubeW, b = res.Flanges[i + 1].QFromTubeW;
                target[i] = 0.5 * (a + b);     // SegmentSolver 两端挂同一个值，取均值
            }
            draws ??= new double[c.SegmentCount];
            for (int i = 0; i < c.SegmentCount; i++)
                draws[i] = (1 - omega) * draws[i] + omega * target[i];

            progress?.Report($"外层耦合 {outer + 1}/{c.CoupleMaxRounds}（ω={omega:0.00}）：回灌法兰抽热…");
            var next = RunOnce(c, progress, cancel, (double[])draws.Clone());
            if (!next.Ok) return next;
            delta = Enumerable.Range(0, c.SegmentCount)
                .Max(i => Math.Abs(next.Segments[i].TRootC - res.Segments[i].TRootC));
            res = next;
            if (delta < c.CoupleTolK)
            {
                res.Notes.Add($"外层耦合 {outer + 1} 轮收敛（管根温差 {delta:0.00} K，ω={omega:0.00}）");
                res.Converged = true;
                break;
            }
        }
        if (!res.Converged)
        {
            res.Notes.Add($"★ 外层耦合 {c.CoupleMaxRounds} 轮未收敛（管根温差仍 {delta:0.0} K，ω={omega:0.00}）——" +
                          "本次结果的每个数都不可用：要么再降 ω / 加轮数，要么该工况确实热失控");
            res.Message = "段↔法兰耦合未收敛";
        }
        return res;
    }

    private static LineResult RunOnce(LineCase c, IProgress<string>? progress,
                                      CancellationToken cancel, double[]? drawIn)
    {
        var res = new LineResult { BaselineMassG = c.BaselineMassG };
        int n = c.SegmentCount, nf = c.FlangeCount;
        if (n < 1) { res.Ok = false; res.Message = "段数不能为 0"; return res; }
        if (c.UseMeasuredCurrent && c.MeasuredCurrentA.Length < n)
        { res.Ok = false; res.Message = $"实测电流只给了 {c.MeasuredCurrentA.Length} 个，需要 {n} 个"; return res; }
        if (c.FlangeFile3dm.Length == 0 && c.FlangePlates.Length == 0)
        { res.Ok = false; res.Message = "未指定法兰几何（.3dm 或解析 FlangePlate 二选一）"; return res; }

        // 各段两端的法兰抽热 W（由壳温度场回灌）。首轮未知，置 0；
        // ★ 必须显式回灌：不设 FlangeDrawOverrideSet 时 SegmentSolver 会**静默回退到
        //   已作废的一维环形模型 FlangeRadial**（§5、§7 记过两次，这是第三次）。
        var drawW = drawIn ?? new double[n];

        // ── ①② 逐段
        var segs = new SegmentOut[n];
        var amps = new double[n];
        var segParams = new DesignInputs[n];   // 各段实际用的参数，判据 ①④ 要拿去复用
        double tg = c.GlassInC;
        for (int i = 0; i < n; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report($"段 {i + 1}/{n}：{(c.UseMeasuredCurrent ? "按实测电流求解" : "反算电流")}…");

            var p = SegmentSolver.Clone(c.Base);
            p.TubeIdMm = c.TubeIdMm; p.WallMinMm = c.WallMm; p.TubeLengthMm = c.SegLengthMm;
            p.SupportSpanMm = c.SegLengthMm; p.GradeName = c.GradeName;
            p.TSetC = c.SetpointC[i]; p.TGlassInC = tg;
            p.GlassHeadM = i < c.HeadM.Length ? c.HeadM[i] : 0;
            p.SizeWall = false;
            p.FlangeDrawOverrideW = drawW[i]; p.FlangeDrawOverrideSet = true;

            SolveResult sr;
            if (c.UseMeasuredCurrent)
            {
                // 实测模式：电流已知，不需要外层二分 —— 这是秒级的来源
                sr = SegmentSolver.SolveAtCurrent(p, c.MeasuredCurrentA[i]);
            }
            else
            {
                sr = SegmentSolver.Solve(p);
            }
            if (!sr.Ok) { res.Ok = false; res.Message = $"段 {i + 1} 求解失败：{sr.Message}"; return res; }

            amps[i] = sr.CurrentA;
            segParams[i] = p;
            double area = Math.PI * (Math.Pow(c.TubeIdMm * 0.5 + c.WallMm, 2)
                                     - Math.Pow(c.TubeIdMm * 0.5, 2));
            segs[i] = new SegmentOut
            {
                Name = $"HC{i + 1}",
                SetpointC = c.SetpointC[i],
                CurrentA = sr.CurrentA,
                PowerW = sr.PowerTotalW,
                TubeJAPerMm2 = sr.CurrentA / area,
                TRootC = sr.TFlangeAC,
                RootDeltaK = c.SetpointC[i] - sr.TFlangeAC,
                GlassInC = tg,
                GlassOutC = sr.TGlassOutC,
                MassG = area * c.SegLengthMm * Materials.PtDensity * 1e-6,
                X = sr.X, TMetal = sr.TMetal, TGlass = sr.TGlass
            };
            tg = sr.TGlassOutC;
        }
        res.Segments = segs;
        res.GlassDropModelK = c.GlassInC - tg;
        res.GlassDropMeasuredK = c.GlassInC - c.GlassOutMeasuredC;

        // ── ③ 逐片法兰
        var flanges = new FlangeOut[nf];
        for (int j = 0; j < nf; j++)
        {
            cancel.ThrowIfCancellationRequested();
            bool analytic = c.FlangePlates.Length > 0;
            var plate = analytic ? c.FlangePlates[Math.Min(j, c.FlangePlates.Length - 1)] : null;
            string file = analytic ? "" : c.FlangeFile3dm[Math.Min(j, c.FlangeFile3dm.Length - 1)];
            double planeY = j < c.FlangePlaneY.Length ? c.FlangePlaneY[j] : double.NaN;
            progress?.Report(analytic
                ? $"法兰 {j + 1}/{nf}：解析几何 + 建网格 + 解场…"
                : $"法兰 {j + 1}/{nf}：提厚度场 + 建网格 + 解场…");

            double holeR = c.TubeIdMm * 0.5 + c.WallMm;
            ShellMesh mesh;
            if (analytic)
            {
                // 管孔必须跟着管外径走，否则法兰与管子对不上
                plate!.HoleRadiusMm = holeR;
                mesh = FlangeMesher.Build(plate, 0, c.MeshFineMm, c.MeshCoarseMm, c.MeshFineRadiusMm);
            }
            else
            {
                var tf = Geometry3dm.LoadThickness(file, c.FlangeLayer, planeY, c.ThicknessStepMm);
                // 厚度标度：.3dm 的**形状**固定，但整体厚度可按比例缩放。
                // 这让「自动定厚」在 .3dm 模式下同样可用 —— 求出的不是绝对厚度，
                // 而是「你这张图纸的厚度要整体 ×k」，工程师照着改一版图即可。
                // t=0（无材料：轮廓外、管孔、开槽）乘任何数仍是 0，故槽与轮廓不受影响。
                double k = j < c.ThicknessScale.Length ? c.ThicknessScale[j] : 1.0;
                if (Math.Abs(k - 1.0) > 1e-9)
                {
                    var scaled = new double[tf.T.Length];
                    for (int q = 0; q < tf.T.Length; q++) scaled[q] = tf.T[q] * k;
                    tf = new ThicknessField
                    {
                        X0 = tf.X0, Z0 = tf.Z0, Step = tf.Step,
                        Nx = tf.Nx, Nz = tf.Nz, T = scaled
                    };
                }
                mesh = FlangeMesher.BuildFromField(tf, holeR, 0,
                            c.MeshFineMm, c.MeshCoarseMm, c.MeshFineRadiusMm);
            }

            double iJoint = LineSolver.JointCurrentA(amps, j);
            var sc = ShellCurrent.Solve(mesh, iJoint,
                        Materials.PtResistivity(c.SetpointC[Math.Min(j, n - 1)]) * 1e3,
                        c.SetpointC[Math.Min(j, n - 1)]);

            // 管根温度取相邻段中较高者（保守：抽热更大）
            double tRoot = j == 0 ? segs[0].TRootC
                         : j >= n ? segs[n - 1].TRootC
                         : Math.Max(segs[j - 1].TRootC, segs[j].TRootC);

            var p2 = SegmentSolver.Clone(c.Base);
            p2.TSetC = c.SetpointC[Math.Min(j, n - 1)];
            // 保温分界：解析几何用该片自己的分界（可为「全裸」= +∞ 之外），
            // .3dm 路径沿用现场实况「仅圆盘保温、舌片裸露」的切点。
            double insulX = analytic ? plate!.InsulBoundaryXResolved
                                     : new FlangePlate().InsulBoundaryXResolved;
            var th = ShellThermal.Solve(mesh, sc.JMagAPerMm2, p2, tRoot, insulX);

            flanges[j] = new FlangeOut
            {
                Name = j == 0 ? "入口" : j >= n ? "出口" : $"{segs[j - 1].Name}|{segs[j].Name}",
                Shared = j > 0 && j < n,
                CurrentA = iJoint,
                MassG = mesh.VolumeMm3 * Materials.PtDensity * 1e-6,
                JMaxAPerMm2 = sc.JMaxAPerMm2,
                Phi = th.PhiOverall,
                QFromTubeW = th.QFromTubeW,
                QGenW = th.QGenW, QLossW = th.QLossW, TRootC = tRoot,
                TMaxC = th.TMaxC, TMinC = th.TMinC, TTabEndC = th.TTabEndMeanC,
                AreaMm2 = mesh.TotalArea, VolumeMm3 = mesh.VolumeMm3,
                CellCount = mesh.CellCount,
                Mesh = mesh, JField = sc.JMagAPerMm2, TField = th.T,
                Source = analytic
                    ? $"解析 Ø{2 * plate!.DiscRadiusMm:0}/舌{-plate.TabEndXMm:0}/t{plate.ThicknessMm:0.00}"
                    : System.IO.Path.GetFileName(file)
            };
            if (sc.ConservationError > 1e-3)
                res.Notes.Add($"{flanges[j].Name}：电流守恒误差 {sc.ConservationError:E2}，偏大");
            if (!th.Converged)
                res.Notes.Add($"{flanges[j].Name}：温度场未收敛（残差 {th.Residual:E2}）");
        }
        res.Flanges = flanges;

        // ── ④ 汇总与判定
        res.TubeMassG = segs.Sum(s => s.MassG);
        res.FlangeMassG = flanges.Sum(f => f.MassG);
        res.TotalMassG = res.TubeMassG + res.FlangeMassG;
        res.SavingPct = c.BaselineMassG > 0
            ? (c.BaselineMassG - res.TotalMassG) / c.BaselineMassG * 100 : 0;

        res.Checks = Judge(c, res, segs, flanges, segParams);
        return res;
    }

    /// <summary>
    /// HANDOVER §4.2k 的判据体系（取代旧的以 J 为中心的那套）：
    ///
    /// <code>
    /// ① 升温：空管 3 h 到 1150 °C        ← 决定最小截面（额定电流）
    /// ② 法兰温度 ≤ 管温（Φ ≤ 1）         ← 硬安全线，不可越
    /// ③ 管根温差 0 &lt; ΔT ≤ 10 K          ← 优化目标，从 ② 的安全侧逼近
    /// ④ 强度利用率 ≤ 1                   ← 真实工况下极宽松
    /// </code>
    ///
    /// **③ 是单边的**：旧代码判 |ΔT| ≤ 10，于是 ΔT = −8 K（法兰比管热 8 K，正在往烧断走）
    /// 会判「✓ 通过」。ΔT ≤ 0 与 Φ &gt; 1 是同一件事的两个视角，两条都列，
    /// 因为一条按段给（看得出卡在哪段），一条按片给（看得出卡在哪片）。
    ///
    /// **J 不再单独判**（§4.2k）：它在 ① 里是额定工况的能力指标，稳态只是参考量；
    /// 且 J_allow = 10 的物理依据本身待定（§4.2i）。故降级为 Reference，只报数不判。
    /// </summary>
    private static ConstraintOut[] Judge(LineCase c, LineResult res, SegmentOut[] segs,
                                         FlangeOut[] flanges, DesignInputs[] segParams)
    {
        var checks = new List<ConstraintOut>();
        int n = segs.Length;

        // ── ① 升温：空管能否在限时内到目标温度
        //    法兰随管一起被加热，且**自身也发热**，故用本算例真实的法兰质量与自身焦耳热
        //    （RampSolver 据此反推法兰电阻）。这比 --ramp 另跑一次稳态耦合解取参考值更准。
        if (c.CheckRamp)
        {
            RampResult? worst = null; string where = "";
            for (int i = 0; i < n; i++)
            {
                double massPairG = flanges[i].MassG + flanges[i + 1].MassG;
                // ★ 折算到**段电流**基准：RampSolver 由 R_f = QGen_ref / I_ref² 反推法兰电阻，
                //   而它拿到的 I_ref 是段电流；共用片实际走的是 √3 倍的接头电流（§4.2h）。
                //   直接把共用片的 QGen 配段电流会把 R_f 高估 (√3)² = 3 倍。
                //   故逐片按各自电流折算 QGen → 段电流下的等效值，再取两片均值（RampSolver 内部 ×2）。
                double GenAtSegCurrent(FlangeOut f, double iSeg)
                    => f.CurrentA > 1e-9 ? f.QGenW * (iSeg / f.CurrentA) * (iSeg / f.CurrentA) : 0;
                double genRefW = 0.5 * (GenAtSegCurrent(flanges[i], segs[i].CurrentA)
                                        + GenAtSegCurrent(flanges[i + 1], segs[i].CurrentA));
                var rr = RampSolver.Solve(segParams[i], c.WallMm, massPairG, genRefW,
                                          segs[i].CurrentA, segs[i].SetpointC,
                                          c.RampFromC, c.RampTargetC, c.RampHours);
                // 最不利 = 升不到的优先，其次用时最长
                bool worse = worst is null
                    || (!rr.Reached && worst.Reached)
                    || (rr.Reached == worst.Reached &&
                        (rr.Reached ? rr.HoursToTarget > worst.HoursToTarget : rr.TPeakC < worst.TPeakC));
                if (worse) { worst = rr; where = segs[i].Name; }
            }
            if (worst is not null)
                checks.Add(new ConstraintOut
                {
                    Name = "① 升温 空管到目标", Unit = "h", Kind = CheckKind.HardSafety,
                    Actual = worst.Reached ? worst.HoursToTarget : double.NaN,
                    Limit = c.RampHours, Ok = worst.Reached && worst.HoursToTarget <= c.RampHours,
                    Where = where,
                    Note = worst.Reached
                        ? $"{c.RampFromC:0}→{c.RampTargetC:0} °C，J={worst.JAPerMm2:0.00}" +
                          (worst.StabilityLimited ? "（电流被热稳定极限压低，不是故障）" : "")
                        : worst.Note
                });
        }

        // ── ② 硬安全线：法兰温度 ≤ 管温（用户原话）。**按温度直接判，不用 Φ 代理**。
        //
        //    ⚠ §4.2k 写的「等价于 Φ ≤ 1」只在**舌片末端绝热**时成立。
        //      Φ = 自身发热 / 自身**表面**散热，不含铜排夹带走的导热。
        //      夹冷一开（BusbarClampTempC ≥ 0），法兰变冷 ⇒ 表面散热变小 ⇒ Φ **反而升高**，
        //      而它其实更安全了。实测到的反例：夹持 80 °C 时 HC2|HC3 片 Φ = 1.313，
        //      但管孔净流入 +47 W（仍在从管子抽热，方向安全）。
        //      故硬安全线判 T_max(法兰) − T_root(管)，Φ 降为佐证。
        var hottest = flanges.OrderByDescending(f => f.TMaxC - f.TRootC).First();
        checks.Add(new ConstraintOut
        {
            Name = "② 法兰最高温 − 管温", Unit = "K", Kind = CheckKind.HardSafety,
            Actual = hottest.TMaxC - hottest.TRootC, Limit = 0, Ok = hottest.TMaxC <= hottest.TRootC + 1e-6,
            Where = hottest.Name,
            Note = $"法兰 {hottest.TMaxC:0.0} °C vs 管根 {hottest.TRootC:0.0} °C；" +
                   $"Φ={hottest.Phi:0.000}（发热 {hottest.QGenW:0} / 表面散热 {hottest.QLossW:0} W）；" +
                   $"管孔净流入 {hottest.QFromTubeW:+0;-0} W" +
                   (hottest.TMaxC > hottest.TRootC
                        ? "。**热量向管子倒灌 —— 这是烧断的过程，不是数值不好看**" : "") +
                   (hottest.TMaxC > 1768 ? $" ★已超铂熔点 1768 °C" : "")
        });

        // ── ②′ 同一条安全线的管侧视角：管根温差必须为**正**（法兰比管冷）
        var coldest = segs.OrderBy(s => s.RootDeltaK).First();
        checks.Add(new ConstraintOut
        {
            Name = "②′管根温差 须为正", Unit = "K", Kind = CheckKind.HardSafety,
            Actual = coldest.RootDeltaK, Limit = 0, LessIsBetter = false,
            Ok = coldest.RootDeltaK > 0, Where = coldest.Name,
            Note = coldest.RootDeltaK <= 0 ? "管根比控温点还热 ⇒ 法兰在加热管子" : ""
        });

        // ── ③ 目标：温差从安全侧逼近 10 K
        var deepest = segs.OrderByDescending(s => s.RootDeltaK).First();
        checks.Add(new ConstraintOut
        {
            Name = "③ 管根温差 ≤ 上限", Unit = "K", Kind = CheckKind.Target,
            Actual = deepest.RootDeltaK, Limit = c.RootDeltaMaxK,
            Ok = deepest.RootDeltaK <= c.RootDeltaMaxK, Where = deepest.Name,
            Note = "冷点越深，玻璃向管壁放的热越多（§6 ②）"
        });

        // ── ④ 强度利用率（管）。法兰不承重 —— 铂管由氧化铝管托底（§4.2d，用户确认），
        //     故此处只校核管，不校核法兰舌片。
        double utilMax = 0; string utilWhere = ""; bool undetermined = false; string undetNote = "";
        for (int i = 0; i < n; i++)
        {
            var mr = Mechanics.Check(segParams[i], c.WallMm, 2.0, new FlangePlate());
            Mechanics.ApplyAllowable(mr, segParams[i], segs[i].SetpointC, segs[i].SetpointC);
            if (double.IsNaN(mr.TubeAllowMPa))
            {
                undetermined = true;
                var g = MaterialDb.Get(segParams[i].GradeName);
                undetNote = $"{segs[i].Name} {segs[i].SetpointC:0} °C 落在 {segParams[i].GradeName} " +
                            $"持久强度实测区间 [{g.CreepTMinC:0}, {g.CreepTMaxC:0}] °C 之外（§6 待补 ③）";
                continue;
            }
            if (mr.TubeUtil > utilMax) { utilMax = mr.TubeUtil; utilWhere = segs[i].Name; }
        }
        checks.Add(new ConstraintOut
        {
            Name = "④ 管强度利用率", Unit = "—", Kind = CheckKind.Target,
            Actual = utilMax, Limit = 1.0, Ok = utilMax <= 1.0 && !undetermined,
            Where = undetermined ? undetNote : utilWhere,
            Undetermined = undetermined,
            Note = "法兰不承重（氧化铝管托底，§4.2d），故不校核舌片"
        });

        // ── 参考量：Φ（②的佐证，夹冷时会失真，见 ② 的说明）
        var worstPhi = flanges.OrderByDescending(f => double.IsNaN(f.Phi) ? -1 : f.Phi).First();
        checks.Add(new ConstraintOut
        {
            Name = "· 法兰自给率 Φ_max", Unit = "—", Kind = CheckKind.Reference, Ok = true,
            Actual = worstPhi.Phi, Limit = 1.0, Where = worstPhi.Name,
            Note = "参考：仅在舌片末端绝热时才等价于 ②；夹冷时 Φ 偏高但更安全"
        });

        // ── 参考量：J 只报数不判（§4.2k）
        var worstJf = flanges.OrderByDescending(f => f.JMaxAPerMm2).First();
        checks.Add(new ConstraintOut
        {
            Name = "· 法兰 J_max", Unit = "A/mm²", Kind = CheckKind.Reference, Ok = true,
            Actual = worstJf.JMaxAPerMm2, Limit = c.Base.JAllowAPerMm2, Where = worstJf.Name,
            Note = "参考：J_allow=10 的物理依据待定（§4.2i），且 §4.2j 已证高 J 不等于局部过热"
        });
        var worstJt = segs.OrderByDescending(s => s.TubeJAPerMm2).First();
        checks.Add(new ConstraintOut
        {
            Name = "· 管 J", Unit = "A/mm²", Kind = CheckKind.Reference, Ok = true,
            Actual = worstJt.TubeJAPerMm2, Limit = c.Base.JAllowAPerMm2, Where = worstJt.Name,
            Note = c.Base.LossScale == 1.0
                ? "参考：散热未标定，本值系统性偏高（§4.2l）"
                : $"参考：散热已按 LossScale={c.Base.LossScale:0.000} 标定"
        });

        // ── 现场验证点：玻璃温降。这是全模型唯一一个拿实测校准的量，必须始终露出来。
        checks.Add(new ConstraintOut
        {
            Name = "· 玻璃温降 vs 实测", Unit = "K", Kind = CheckKind.Reference, Ok = true,
            Actual = res.GlassDropModelK, Limit = res.GlassDropMeasuredK, Where = "整线",
            Note = $"偏差 {res.GlassDropModelK - res.GlassDropMeasuredK:+0.0;-0.0} K"
        });

        return checks.ToArray();
    }
}
