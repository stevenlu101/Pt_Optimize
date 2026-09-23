using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PtOptimize.Core;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  F4（分区热账按材料份额，2026-09-23）门与改前对拍共用的算例搭法 —— 只用 F4 之前就有的公开接口，
//  所以同一份文件拷到改前的源码快照上也能编译，改前／改后两边的算例由同一段代码造（对拍不手抄两份）。
//  F4 的「改回」开关由调用方用 tweak 回调设（本档不引用 F4 新加的成员）。
// ════════════════════════════════════════════════════════════════════════════
internal static class R48F4Cases
{
    /// <summary>等温电流场 + 片 0 壳热解用的电流与温度（与 R48NMeshGateTests 的闭合门同一组：片 0 设计电流 1214 A、1150 °C）；管根取 1140 °C（选定，只为有一个耦合前的热解，不代表工况）。</summary>
    internal const double TRootC = 1140.0;

    /// <summary>带格与闭合门的 18 例（W08 片 0；盘径五档 × 舌半宽 30，舌半宽四档 × 盘径 30；× 导航／判决）。与 R48NMeshGateTests.门_带格恒0_电功率闭合_导航判决 同一份列表。</summary>
    internal static IEnumerable<(double R, double W, string Grade, double Fine)> Closure18()
    {
        var cases = new List<(double R, double w)>();
        foreach (double R in new[] { 30.50, 30.75, 31.00, 31.01, 31.25 }) cases.Add((R, 30.0));
        foreach (double w in new[] { 29.50, 29.55, 29.70, 29.80 }) cases.Add((30.0, w));
        foreach (var (R, w) in cases)
            foreach (var (grade, fine) in new[] { ("导航", 0.0), ("判决", 1.0) })
                yield return (R, w, grade, fine);
    }

    /// <summary>W08 片 0、盘径 R、舌半宽 w、细区 fine 的网格与板件（R48NMeshGateTests.Build 原路，生产网格规则）。</summary>
    internal static (LineCase lc, FlangePlate g, ShellMesh m) ClosureMesh(double R, double w, double fine)
    {
        var d = R48NMeshGateTests.Design("W08").Clone();
        R48NMeshGateTests.SetRW(d, R, w);
        var (lc, g, m, _) = R48NMeshGateTests.Build(d, new DesignInputs(), fine);
        return (lc, g, m);
    }

    /// <summary>片 0：等温电流场（1214 A、1150 °C）→ LineRunner.PlateThermalInputs → LineRunner.SolvePlateThermal（整线同一份配方）。<paramref name="tweak"/> 只给门设 F4 的改回。</summary>
    internal static (ShellCurrentResult sc, ShellThermalResult th) PlateThermal(LineCase lc, ShellMesh m, Action<PlateThermalSetup>? tweak = null)
    {
        double rho = Materials.PtResistivity(R48NMeshGateTests.PlateTempC) * 1e3;
        var sc = ShellCurrent.SolveFor(lc, m, R48NMeshGateTests.PlateCurrentA, rho, R48NMeshGateTests.PlateTempC);
        var ts = LineRunner.PlateThermalInputs(lc, 0, R48NMeshGateTests.PlateCurrentA, null);
        tweak?.Invoke(ts);
        var th = LineRunner.SolvePlateThermal(m, sc.JMagAPerMm2, TRootC, ts);
        return (sc, th);
    }

    /// <summary>
    /// 判决网格整线算例：<paramref name="which"/> = W08／W06；<paramref name="discInsulMm"/> 非 NaN 时全线圆盘保温设成它（与 R48NMeshInjectTests.门f_对拍 同一写法），NaN = 设计原样（现役设计）。
    /// 判决网格 = MeshVerify.RequiredMeshFor(原设计)（门 f 同一口径）。
    /// </summary>
    internal static (LineCase lc, DesignSpec d) JudgeCase(string which, double discInsulMm, Action<LineCase>? tweak = null)
    {
        var d0 = R48NMeshGateTests.Design(which);
        var reqFine = MeshVerify.RequiredFineMmFor(d0);   // （合并 C4′ 时改，变因 = 决 29 自适应：RequiredMeshFor 签名加工艺参数 DesignInputs，细区半径改为计划初值 max(盘半径, 孔半径) + 热长度，W08 53.697 mm；细步单独取 RequiredFineMmFor）
        var d = d0.Clone();
        if (!double.IsNaN(discInsulMm)) { d.FlangeInsulated = true; d.FlangeInsulMm = discInsulMm; d.DiscInsulMm = Array.Empty<double>(); }
        var p = new DesignInputs();
        var dummy = new SolverResult { Design = d };
        Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
        var (_, reqRadius) = MeshVerify.RequiredMeshFor(d, p);   // 合并 C4′ 时改：计划初值（不放大；门 f 同一口径）
        var lc = d.BuildCase(p);
        Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = reqFine, FineRadiusMm = reqRadius });
        tweak?.Invoke(lc);
        return (lc, d);
    }

    /// <summary>整线全量转储（R48LineDumpTests.Dump，含文字），两处挂钟改写成占位（与 R48PropsWiringGateTests 门 1 同一写法）。</summary>
    internal static string DumpLine(LineCase lc, LineResult r)
    {
        string full = Regex.Replace(R48LineDumpTests.Dump(lc, r, withText: true), @"(结果\.JacobianAmpSec = )[^\n]*", "$1<耗时>");
        return Regex.Replace(full, @"用时 [0-9.]+ s", "用时 <耗时> s");
    }

    internal static string Sha(string s) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(s))).ToLowerInvariant();

    /// <summary>壳热解结果的全量转储：公开字段与属性逐个写（double 用 R 格式、数组写个数 + SHA-256、配方逐属性），成员按名字排序。</summary>
    internal static string DumpThermal(ShellThermalResult th)
    {
        var sb = new StringBuilder();
        void Val(string path, object? v)
        {
            switch (v)
            {
                case null: sb.Append(path).Append(" = null\n"); return;
                case double x: sb.Append(path).Append(" = ").Append(x.ToString("R", CultureInfo.InvariantCulture)).Append('\n'); return;
                case bool b: sb.Append(path).Append(" = ").Append(b ? "true" : "false").Append('\n'); return;
                case string s: sb.Append(path).Append(" = \"").Append(s.Replace("\n", "\\n")).Append("\"\n"); return;
                case Enum e: sb.Append(path).Append(" = ").Append(e.GetType().Name).Append('.').Append(e).Append('\n'); return;
                case int or long or short or byte: sb.Append(path).Append(" = ").Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append('\n'); return;
                case double[] a: sb.Append(path).Append(" = <").Append(a.Length).Append(" 个，SHA-256 ").Append(Sha(string.Join("\n", a.Select(x => x.ToString("R", CultureInfo.InvariantCulture))))).Append(">\n"); return;
                case bool[] a: sb.Append(path).Append(" = <").Append(a.Length).Append(" 个，SHA-256 ").Append(Sha(string.Join("", a.Select(x => x ? '1' : '0')))).Append(">\n"); return;
                case int[] a: sb.Append(path).Append(" = <").Append(a.Length).Append(" 个，SHA-256 ").Append(Sha(string.Join("\n", a))).Append(">\n"); return;
            }
            var t = v.GetType();
            if (v is IEnumerable en) { var items = en.Cast<object?>().ToList(); sb.Append(path).Append(" = <").Append(items.Count).Append(" 项>\n"); for (int i = 0; i < Math.Min(items.Count, 64); i++) Val($"{path}[{i}]", items[i]); return; }
            foreach (var mem in Members(t)) Val(path + "." + mem.Name, mem.Get(v));
        }
        foreach (var mem in Members(typeof(ShellThermalResult))) Val(mem.Name, mem.Get(th));
        return sb.ToString();
    }

    private static IEnumerable<(string Name, Func<object, object?> Get)> Members(Type t)
    {
        var list = new List<(string, Func<object, object?>)>();
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) list.Add((f.Name, o => f.GetValue(o)));
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == 0 && p.CanRead) list.Add((p.Name, o => p.GetValue(o)));
        return list.OrderBy(x => x.Item1, StringComparer.Ordinal);
    }
}
