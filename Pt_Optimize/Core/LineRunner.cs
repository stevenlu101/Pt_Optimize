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
    public double[] MeasuredCurrentA = { 1655, 1510, 1446 };

    // ── 法兰（每片一个 .3dm，长度 = 段数+1；可重复同一文件）
    public string[] FlangeFile3dm = Array.Empty<string>();
    public string FlangeLayer = "法兰";
    /// <summary>每片在 .3dm 中的平面 Y；NaN = 取该图层第一片</summary>
    public double[] FlangePlaneY = Array.Empty<double>();
    /// <summary>厚度场提取步长 mm（1.0 足够分辨槽与阶梯）</summary>
    public double ThicknessStepMm = 1.0;

    // ── 网格
    public double MeshFineMm = 2.0, MeshCoarseMm = 11.0, MeshFineRadiusMm = 50.0;

    // ── 玻璃与验证
    public double GlassInC = 1150, GlassOutMeasuredC = 1130;

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
    public double AreaMm2, VolumeMm3;
    public int CellCount;
    public ShellMesh? Mesh;
    public double[] JField = Array.Empty<double>();
    public double[] TField = Array.Empty<double>();
    public string Source = "";        // 用了哪个 .3dm
}

public sealed class ConstraintOut
{
    public string Name = "", Where = "", Unit = "";
    public double Actual, Limit;
    public bool Ok;
    /// <summary>true = 实际值须 ≤ 限值；false = 须 ≥ 限值</summary>
    public bool LessIsBetter = true;
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
        //    实测电流模式下两边都不用二分，故 2–3 轮仍是秒级。
        for (int outer = 0; outer < 3; outer++)
        {
            cancel.ThrowIfCancellationRequested();
            var draws = new double[c.SegmentCount];
            for (int i = 0; i < c.SegmentCount; i++)
            {
                // 段 i 的两端分别是法兰 i 与 i+1，各贡献自己的抽热
                double a = res.Flanges[i].QFromTubeW, b = res.Flanges[i + 1].QFromTubeW;
                draws[i] = 0.5 * (a + b);      // SegmentSolver 两端挂同一个值，取均值
            }
            progress?.Report($"外层耦合 {outer + 1}/3：回灌法兰抽热…");
            var next = RunOnce(c, progress, cancel, draws);
            if (!next.Ok) return next;
            double delta = Enumerable.Range(0, c.SegmentCount)
                .Max(i => Math.Abs(next.Segments[i].TRootC - res.Segments[i].TRootC));
            res = next;
            if (delta < 1.0) { res.Notes.Add($"外层耦合 {outer + 1} 轮收敛（管根温差 {delta:0.00} K）"); break; }
            if (outer == 2) res.Notes.Add($"外层耦合 3 轮未完全收敛（管根温差 {delta:0.0} K）");
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
        if (c.FlangeFile3dm.Length == 0)
        { res.Ok = false; res.Message = "未指定法兰 .3dm"; return res; }

        // 各段两端的法兰抽热 W（由壳温度场回灌）。首轮未知，置 0；
        // ★ 必须显式回灌：不设 FlangeDrawOverrideSet 时 SegmentSolver 会**静默回退到
        //   已作废的一维环形模型 FlangeRadial**（§5、§7 记过两次，这是第三次）。
        var drawW = drawIn ?? new double[n];

        // ── ①② 逐段
        var segs = new SegmentOut[n];
        var amps = new double[n];
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
            p.SizeWall = false; p.SizeFlangeThickness = false;
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
            string file = c.FlangeFile3dm[Math.Min(j, c.FlangeFile3dm.Length - 1)];
            double planeY = j < c.FlangePlaneY.Length ? c.FlangePlaneY[j] : double.NaN;
            progress?.Report($"法兰 {j + 1}/{nf}：提厚度场 + 建网格 + 解场…");

            var tf = Geometry3dm.LoadThickness(file, c.FlangeLayer, planeY, c.ThicknessStepMm);
            double holeR = c.TubeIdMm * 0.5 + c.WallMm;
            var mesh = FlangeMesher.BuildFromField(tf, holeR, 0,
                            c.MeshFineMm, c.MeshCoarseMm, c.MeshFineRadiusMm);

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
            // 保温分界：默认「仅圆盘保温、舌片裸露」（现场实况），取切点
            double insulX = new FlangePlate().InsulBoundaryXResolved;
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
                TMaxC = th.TMaxC, TMinC = th.TMinC, TTabEndC = th.TTabEndMeanC,
                AreaMm2 = mesh.TotalArea, VolumeMm3 = mesh.VolumeMm3,
                CellCount = mesh.CellCount,
                Mesh = mesh, JField = sc.JMagAPerMm2, TField = th.T,
                Source = System.IO.Path.GetFileName(file)
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

        var checks = new List<ConstraintOut>();
        var worstRoot = segs.OrderByDescending(s => Math.Abs(s.RootDeltaK)).First();
        checks.Add(new ConstraintOut
        {
            Name = "法兰衔接处温差", Unit = "K", Actual = Math.Abs(worstRoot.RootDeltaK),
            Limit = 10, Ok = Math.Abs(worstRoot.RootDeltaK) <= 10, Where = worstRoot.Name
        });
        var worstJf = flanges.OrderByDescending(f => f.JMaxAPerMm2).First();
        checks.Add(new ConstraintOut
        {
            Name = "法兰电流密度", Unit = "A/mm²", Actual = worstJf.JMaxAPerMm2,
            Limit = c.Base.JAllowAPerMm2, Ok = worstJf.JMaxAPerMm2 <= c.Base.JAllowAPerMm2,
            Where = worstJf.Name
        });
        var worstJt = segs.OrderByDescending(s => s.TubeJAPerMm2).First();
        checks.Add(new ConstraintOut
        {
            Name = "管电流密度", Unit = "A/mm²", Actual = worstJt.TubeJAPerMm2,
            Limit = c.Base.JAllowAPerMm2, Ok = worstJt.TubeJAPerMm2 <= c.Base.JAllowAPerMm2,
            Where = worstJt.Name
        });
        checks.Add(new ConstraintOut
        {
            Name = "玻璃温降 vs 实测", Unit = "K", Actual = res.GlassDropModelK,
            Limit = res.GlassDropMeasuredK, Ok = Math.Abs(res.GlassDropModelK - res.GlassDropMeasuredK) <= 5,
            Where = "整线"
        });
        res.Checks = checks.ToArray();
        return res;
    }
}
