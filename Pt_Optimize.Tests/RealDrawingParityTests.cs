using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// R47 复修 M2／M4（2026-09-13）：**真图纸**走 Rhino 探针（Pt_Optimize.Geom thickness 模式）的门。
///   · M2：deliverable/设计记录_管壁0.8mm.3dm「入口」层 vs DesignSpec.Builtin[0] 片 0，同输入：
///     图纸网格面积差 &lt; 0.3 %、抽热差 &lt; 1 W、发热差 &lt; 1 %。病：采样点恰落在包围盒面上（舌尖 x=−140、直边 z=±30）时射线擦边、
///     整列整排判成无料（实测 入口 层步 1／0.5／0.25 的栅格面积 7550／7636／7679 mm² 随步长变；修后 7641／7682／7703）。
///   · M4：同一张图走 MeshVerify.Run(Shape, wall, 工厂) 至少 3 档不抛；每档探针用时与点数写进记录。
/// 探针不在（Pt_Optimize.Geom 没构建）就抛：断言失去了对象，不能算通过。
/// </summary>
public class RealDrawingParityTests
{
    private static string DrawingPath()
    {
        string f = Path.Combine(HandoverDoc.Root(), "deliverable", "设计记录_管壁0.8mm.3dm");
        if (!File.Exists(f)) throw new FileNotFoundException("交付件不见了：" + f);
        if (Geometry3dm.FindProbe() is null) throw new FileNotFoundException("找不到 Pt_Optimize.Geom.exe —— 真图纸的门跑不了，不能算通过");
        return f;
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 真图纸入口层与解析板同输入_面积差小于0p3percent_抽热差小于1W_发热差小于1percent()
    {
        string file = DrawingPath();
        var (g, lc, iA, tRoot, tSet, clampC, tabInsul, _) = DrawingPathParityTests.Case(1);
        double hF = lc.MeshFineMm, hC = lc.MeshCoarseMm, rF = lc.MeshFineRadiusMm, cl = lc.Base.BusbarClampLengthMm;
        double hFinest = lc.MeshInnerMm > 1e-9 ? Math.Min(hF, lc.MeshInnerMm) : hF;
        double step = Math.Min(lc.ThicknessStepMm, FlangeMesher.RasterStepForFile(hFinest));   // 与 LineRunner 同口径
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var f = Geometry3dm.LoadThickness(file, "入口", double.NaN, step);
        sw.Stop();
        Assert.True(f.HasExactEnvelope, "探针必须给精确材料包络");
        Assert.Equal(g.TabTipXMm, f.XMinMaterial, 3);
        Assert.Equal(g.DiscRadiusMm, f.XMaxMaterial, 3);

        var mA = FlangeMesher.Build(g, 0, hF, hC, rF, cl, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
        var mF = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, hF, hC, rF, cl, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
        double areaDiff = Math.Abs(mF.TotalArea - mA.TotalArea) / mA.TotalArea;
        var a = DrawingPathParityTests.SolveOne(mA, lc, g, iA, tRoot, tSet, clampC, tabInsul);
        var b = DrawingPathParityTests.SolveOne(mF, lc, g, iA, tRoot, tSet, clampC, tabInsul);
        string line = $"真图纸 入口 层（栅格步 {step}，探针 {sw.Elapsed.TotalSeconds:0.0} s，{f.Nx}×{f.Nz} 点）：网格面积 {mF.TotalArea:0.0} vs 解析 {mA.TotalArea:0.0} mm²（差 {areaDiff * 100:0.000} %）；" +
                      $"抽热 {b.QFromTube:0.000} vs {a.QFromTube:0.000} W；发热 {b.QGen:0.0} vs {a.QGen:0.0} W；格数 {b.Cells} vs {a.Cells}";
        Console.WriteLine(line);
        File.AppendAllText(Path.Combine(HandoverDoc.Root(), "deliverable", "R47_真图纸对拍_2026-09-13.txt"),
                           $"{DateTime.Now:yyyy-MM-dd HH:mm}　{line}{Environment.NewLine}", new UTF8Encoding(false));
        Assert.True(areaDiff < 0.003, $"图纸网格面积差 {areaDiff * 100:0.000} % ≥ 0.3 %（图纸 {mF.TotalArea:0.0}，解析 {mA.TotalArea:0.0}）");
        Assert.True(Math.Abs(a.QFromTube - b.QFromTube) < 1.0, $"抽热差 {Math.Abs(a.QFromTube - b.QFromTube):0.000} W ≥ 1 W（图纸 {b.QFromTube:0.000}，解析 {a.QFromTube:0.000}）");
        Assert.True(Math.Abs(a.QGen - b.QGen) / a.QGen < 0.01, $"发热差 {Math.Abs(a.QGen - b.QGen) / a.QGen * 100:0.00} % ≥ 1 %（图纸 {b.QGen:0.0}，解析 {a.QGen:0.0}）");
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 真图纸走加密复算至少三档不抛_量每档探针用时与点数()
    {
        string file = DrawingPath();
        var p = new DesignInputs();
        var d0 = DesignSpec.Builtin[0].Clone();
        d0.SetpointC = new[] { 1150.0 }; d0.SegLengthMm = new[] { 300.0 };
        d0 = d0.Fit();
        var lc0 = d0.BuildCase(p, checkRamp: false);
        double holeR = lc0.TubeIdMm * 0.5 + lc0.WallMm;
        var g0 = d0.Plate(0, d0.DiscFloorMm(p)); g0.HoleRadiusMm = holeR;
        lc0.FlangePlates = Array.Empty<FlangePlate>();
        lc0.FlangeFile3dm = new[] { file, file };
        lc0.FlangeLayer = "入口";
        lc0.GeomForJudge = new[] { g0, g0 };
        lc0.TabInsul3dmPerPlateMm = new[] { d0.TabInsulMm[0], d0.TabInsulMm[0] };

        var f0 = Geometry3dm.LoadThickness(file, "入口", double.NaN, 0.5);
        var sh = PlateShapeAnalyzer.Analyze(f0);
        var (h0, radius, innerR, refused) = MeshVerify.RequiredMeshFor(sh, lc0.WallMm);
        Assert.Null(refused);
        var sb = new StringBuilder();
        sb.AppendLine($"R47 复修 M4 真图纸加密复算（{Path.GetFileName(file)}「入口」层，1 段两片）　{DateTime.Now:yyyy-MM-dd HH:mm}　起始网格 {h0:0.000} mm　中带半径 {radius:0.0}　内带半径 {innerR:0.0}");
        // 每档探针单独计时（LoadThickness 缓存键含步长：这里量过之后 Run 里命中缓存，Run 的耗时就是纯场解）
        double h = h0;
        for (int it = 0; it < 3; it++)
        {
            double step = Math.Min(lc0.ThicknessStepMm, FlangeMesher.RasterStepForFile(Math.Min(h0, h)));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var f = Geometry3dm.LoadThickness(file, "入口", double.NaN, step);
            sw.Stop();
            string line = $"第 {it + 1} 档：内带 {h:0.000} mm ⇒ 栅格步 {step:0.###} mm　探针 {sw.Elapsed.TotalSeconds:0.0} s　{f.Nx}×{f.Nz} = {f.Nx * f.Nz} 点（有料 {f.T.Count(v => v > 1e-9)}）{(FlangeMesher.RasterAtFloor(Math.Min(h0, h)) ? "　已到地板" : "")}";
            Console.WriteLine(line); sb.AppendLine(line);
            h = MeshAdapt.Refine(h);
        }
        var res = MeshVerify.Run(sh, lc0.WallMm, (hMid, hInner) =>
        {
            var lc = FlangeAutoSizer.CloneCase(lc0);
            lc.MeshFineMm = hMid; lc.MeshFineRadiusMm = radius; lc.MeshInnerMm = hInner; lc.MeshInnerRadiusMm = innerR;
            return lc;
        }, maxCells: 40000, maxRounds: 3, progress: new Progress<string>(s => { Console.WriteLine(s); sb.AppendLine(s); }));
        Assert.NotNull(res.Line);
        Assert.True(res.Trace.Count >= 3, $"只跑了 {res.Trace.Count} 档：{res.Verdict}");
        for (int i = 1; i < res.Trace.Count; i++)
            Assert.True(res.Trace[i].Cells > res.Trace[i - 1].Cells, $"第 {i + 1} 档 {res.Trace[i].Cells} 格应多于上一档 {res.Trace[i - 1].Cells}");
        foreach (var t in res.Trace)
            sb.AppendLine($"档 {t.Fine:0.000} mm：{t.Cells} 格　管孔净流入 {t.N2p:0.000} W　最热铂高出热偶读数 {t.N2pp:0.000} K　管根低于热偶读数 {t.N3:0.000} K　场解 {t.Sec:0} s");   // R48 B（2026-09-14 Opus 5）：MeshVerify.Trace 的两列换成热偶读数基准的新判据，标签跟着换
        sb.AppendLine(res.Verdict);
        if (res.MidBandConfirm is not null) sb.AppendLine(res.MidBandConfirm);
        var floorNotes = res.Line!.Notes.Where(n => n.Contains("已到图纸分辨率", StringComparison.Ordinal)).ToArray();
        sb.AppendLine($"「已到图纸分辨率」附注 {floorNotes.Length} 条");
        File.WriteAllText(Path.Combine(HandoverDoc.Root(), "deliverable", "R47_真图纸加密复算_2026-09-13.txt"), sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>仪器：真图纸厚度场 vs 同一解析板的栅格，逐点比对差在哪（只记录，不判）。</summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 仪器_真图纸与解析板栅格逐点差在哪()
    {
        string file = DrawingPath();
        var (g, lc, _, _, _, _, _, _) = DrawingPathParityTests.Case(1);
        var f = Geometry3dm.LoadThickness(file, "入口", double.NaN, 0.5);
        var sb = new StringBuilder();
        sb.AppendLine($"真图纸 vs 解析板 栅格逐点差（步 0.5）　{DateTime.Now:yyyy-MM-dd HH:mm}　图幅 x0 {f.X0} z0 {f.Z0} {f.Nx}×{f.Nz}");
        int onlyDrawing = 0, onlyAnalytic = 0, both = 0; double tDiffSum = 0; int tDiffN = 0;
        var buckets = new Dictionary<string, int>();
        var samples = new List<string>();
        for (int i = 0; i < f.Nx; i++)
            for (int k = 0; k < f.Nz; k++)
            {
                double x = f.X0 + i * f.Step, z = f.Z0 + k * f.Step;
                double td = f.T[i * f.Nz + k];
                bool inA = g.Inside(x, z);
                double ta = inA ? g.ThicknessAt(x, z) : 0;
                bool inD = td > 1e-9;
                if (inA && inD) { both++; tDiffSum += Math.Abs(td - ta); tDiffN++; if (Math.Abs(td - ta) > 0.05 && samples.Count < 40) samples.Add($"厚差 ({x},{z}) 图 {td:0.000} 解 {ta:0.000}"); continue; }
                if (!inA && !inD) continue;
                double r = Math.Sqrt(x * x + z * z);
                string where = Math.Abs(r - g.HoleRadiusMm) < 0.6 ? "孔边"
                             : Math.Abs(r - g.DiscRadiusMm) < 0.6 && x > 0 ? "盘外缘"
                             : Math.Abs(Math.Abs(z) - 30) < 0.01 ? "直边 z=±30"
                             : Math.Abs(x + 140) < 0.01 ? "舌尖 x=−140"
                             : Math.Abs(r - 28.8) < 0.6 || Math.Abs(r - 31.8) < 0.6 ? "环级分界"
                             : "其他";
                string key = (inD ? "只图纸有料 " : "只解析有料 ") + where;
                buckets[key] = buckets.GetValueOrDefault(key) + 1;
                if (inD) onlyDrawing++; else onlyAnalytic++;
                if (samples.Count < 40) samples.Add($"{key} ({x},{z}) r={r:0.00} 图 {td:0.000} 解 {ta:0.000}");
            }
        sb.AppendLine($"两边都有料 {both}　只图纸有 {onlyDrawing}　只解析有 {onlyAnalytic}　共有点上平均厚差 {(tDiffN > 0 ? tDiffSum / tDiffN : 0):0.0000} mm");
        foreach (var kv in buckets.OrderByDescending(kv => kv.Value)) sb.AppendLine($"  {kv.Key}：{kv.Value} 点（{kv.Value * f.Step * f.Step:0.0} mm²）");
        foreach (var l in samples) sb.AppendLine("  " + l);
        Console.WriteLine(sb.ToString());
        File.WriteAllText(Path.Combine(HandoverDoc.Root(), "deliverable", "R47_真图纸逐点差_2026-09-13.txt"), sb.ToString(), new UTF8Encoding(false));
    }
}
