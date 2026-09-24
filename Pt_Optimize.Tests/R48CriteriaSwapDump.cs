using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using PtOptimize.Core;

namespace PtOptimize.Tests;

/// <summary>
/// 决 103（业主 2026-09-24，判据换向）门与归因共用的**整线解转储**：一次整线解（与 <c>R48NMeshGateTests.SolveLine</c> 同一路：
/// ApplySectionFloor → MeshVerify.RequiredMeshFor → BuildCase → ApplyCaseMesh → LineRunner.Run），把判据表每一行（名、判定级、值、限值、过否、判不了、暂不给数、位置、说明全文）
/// 与逐片热学量按 "R" 格式（往返精确）逐行写出。
/// ⚠ 本档只用决 103 之前就有的接口：同一份代码在改前树（cc49836 + 本档）与改后树上都编得过，改前树的转储才能与改后树「改回」那一跑逐行对拍。
///   挂钟只进以「耗时」开头的那一行，对拍时跳过（去挂钟）。
/// </summary>
internal static class R48CriteriaSwapDump
{
    internal sealed class Out
    {
        public string Text = "";
        public LineResult? R;
        public double Sec;
        public string Error = "";
    }

    static string X(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string B(bool b) => b ? "1" : "0";
    /// <summary>说明文字里的换行与制表会打乱逐行对拍 ⇒ 换成可见记号（只作转储，不改原文）。</summary>
    static string Flat(string? s) => (s ?? "").Replace("\r", "⏎").Replace("\n", "⏎").Replace("\t", "⇥");

    /// <param name="tweak">解之前改参数表的一处（改后树的门拿它设判据口径）；null = 原路。</param>
    internal static Out SolveAndDump(string tag, DesignSpec d, DesignInputs p, double fineMm, Action<DesignInputs>? tweak = null)
    {
        var o = new Out();
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        try
        {
            tweak?.Invoke(p);
            var dummy = new SolverResult { Design = d };
            Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
            var (_, reqRadius) = MeshVerify.RequiredMeshFor(d, p);
            var lc = d.BuildCase(p);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = fineMm, FineRadiusMm = reqRadius });
            var r = LineRunner.Run(lc, null);
            o.R = r;
            sb.AppendLine($"#案例\t{tag}\tR={X(d.DiscRadiusMm)}\tw={X(d.TabHalfWidthMm)}\t舌长={X(d.TabLengthMm)}\t细区mm={X(lc.MeshFineMm)}\t细区半径={X(lc.MeshFineRadiusMm)}");
            sb.AppendLine($"整线\tOk={B(r.Ok)}\tConverged={B(r.Converged)}\tAllOk={B(r.AllOk)}\tHardOk={B(r.HardOk)}\t耦合轮={r.CoupleRounds}\t单元={r.MeshCells}"
                        + $"\t铂重={X(r.TotalMassG)}\t法兰重={X(r.FlangeMassG)}\t认证误差={X(r.CertErrK)}\t容差={X(r.CoupleTolKUsed)}\tMessage={Flat(r.Message)}");
            sb.AppendLine("板厚\t" + string.Join("/", d.TabThickMm.Select(X)));
            foreach (var c in r.Checks)
                sb.AppendLine($"判据\t{c.Name}\t{c.Kind}\t{X(c.Actual)}\t{X(c.Limit)}\t{B(c.Ok)}\t{B(c.Undetermined)}\t{B(c.Withheld)}\t{Flat(c.Where)}\t{Flat(c.Note)}");
            foreach (var f in r.Flanges)
                sb.AppendLine($"片\t{f.Name}\tTMax={X(f.TMaxC)}\tTRoot={X(f.TRootC)}\tTDiscMax={X(f.TDiscMaxC)}\tTTabMax={X(f.TTabMaxC)}\tQFromTube={X(f.QFromTubeW)}"
                            + $"\tQGen={X(f.QGenW)}\t局稳={X(f.LocalStabMargin)}\t局稳r={X(f.LocalStabRMm)}\t局稳T={X(f.LocalStabTempC)}\t局稳L={X(f.LocalStabLatLenMm)}"
                            + $"\t盘峰r={X(f.DiscMaxRMm)}\t舌峰r={X(f.TabMaxRMm)}\t质量={X(f.MassG)}");
            foreach (var s in r.Segments)
                sb.AppendLine($"段\t{s.Name}\t电流={X(s.CurrentA)}\tTRootA={X(s.TRootAC)}\tTRootB={X(s.TRootBC)}\t管J={X(s.TubeJAPerMm2)}");
            foreach (var n in r.Notes)
                sb.AppendLine($"注\t{Flat(n)}");
            foreach (var w in r.Failed)
                sb.AppendLine($"没过\t{Flat(w)}");
        }
        catch (Exception ex)
        {
            o.Error = $"{ex.GetType().Name}：{ex.Message}";
            sb.AppendLine($"#案例\t{tag}\t**抛异常：{Flat(o.Error)}**");
        }
        o.Sec = sw.Elapsed.TotalSeconds;
        sb.AppendLine($"耗时\t{o.Sec:0.0} s（挂钟，对拍时跳过）");
        o.Text = sb.ToString();
        return o;
    }

    /// <summary>
    /// 对拍用：去掉挂钟行与文件头（只留以 #案例／整线／板厚／判据／片／段／注／没过 开头的行），并把说明里的挂钟读数换成记号
    /// （求解器自己写进 Notes 的「量雅可比用时 14.9 s」这类 —— 是机时、不是算出来的数；门 a 首跑两例各差这一行，2026-09-24 加）。
    /// </summary>
    internal static string[] Comparable(string text) => text.Replace("\r\n", "\n").Split('\n')
        .Where(l => l.StartsWith("#案例\t", StringComparison.Ordinal) || l.StartsWith("整线\t", StringComparison.Ordinal)
                 || l.StartsWith("板厚\t", StringComparison.Ordinal) || l.StartsWith("判据\t", StringComparison.Ordinal)
                 || l.StartsWith("片\t", StringComparison.Ordinal) || l.StartsWith("段\t", StringComparison.Ordinal)
                 || l.StartsWith("注\t", StringComparison.Ordinal) || l.StartsWith("没过\t", StringComparison.Ordinal))
        .Select(l => WallClock.Replace(l, "${w} ‹挂钟› s"))
        .ToArray();

    /// <summary>说明文字里的挂钟读数（「用时 14.9 s」「耗时 3.0 s」）。</summary>
    static readonly System.Text.RegularExpressions.Regex WallClock =
        new(@"(?<w>用时|耗时) [0-9]+(\.[0-9]+)? s", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>门与归因共用的两个算例：W08 现役设计（盘 30、舌半宽 30）与决 102 探针里的 R = w = 45（舌长按搜形状规则）。导航网格（FineMm = 0）。</summary>
    internal static IReadOnlyList<(string Tag, DesignSpec D)> Cases()
    {
        var d0 = R48NMeshGateTests.Design("W08");
        var a = d0.Clone(); R48NMeshGateTests.SetRW(a, 30.0, 30.0);
        var b = d0.Clone(); R48NMeshGateTests.SetRW(b, 45.0, 45.0);
        return new[] { ("W08现役_盘30", a), ("W08_R45w45", b) };
    }
}
