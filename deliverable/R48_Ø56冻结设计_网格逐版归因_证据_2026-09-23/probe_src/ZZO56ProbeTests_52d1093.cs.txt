using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// (c) 网格逐版归因探针（2026-09-23，只量不判、只印不改判词；不入库）。
/// 同一冻结设计「盘Ø56 记录（两段）」按 R47NavGridInstrumentTests.Disc56TwoSegs 的写法逐字建（Builtin[0] 克隆 + 覆盖），
/// d.BuildCase(new DesignInputs()) → LineRunner.Run，网格取 BuildCase 给的导航档（印出核对 2／11／50／0）。
/// 同一份源码放进各树，只用各树都有的成员；改回开关一律经反射（该树没有该开关 ⇒ 印「该树无此开关」，不跑）。
/// 环境变量：ZZO56_TAG（树标签）、ZZO56_VARIANTS（逗号分隔：base、F3、F4、SEG、MR:属性名）。
/// MR:属性名 需要该工作树 LineRunner 里本地加的注入钩 ZzoRulesOverride（只在探针工作树里加，缺省 null ⇒ 生产规则逐位不变）。
/// </summary>
public class ZZO56ProbeTests
{
    static DesignSpec Disc56TwoSegs()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 };
        d.TubeInsulMm = 10;
        d.DiscRadiusMm = 28; d.TabHalfWidthMm = 28; d.TabLengthMm = 140;
        double[] tb = { 0.60, 1.02, 0.60 }, tg = { 1.75, 3.03, 1.75 }, ins = { 5.1, 2.8, 8.6 };
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            int k = Math.Min(j, tb.Length - 1);
            d.TabThickMm[j] = tb[k]; if (j < d.TongueThickMm.Length) d.TongueThickMm[j] = tg[k];
            if (j < d.TabInsulMm.Length) d.TabInsulMm[j] = ins[k];
            if (j < d.RingMul.Length) d.RingMul[j] = 1.0;
            if (j < d.SlotSpanDeg.Length) d.SlotSpanDeg[j] = 0; if (j < d.TabHoleRMm.Length) d.TabHoleRMm[j] = 0;
        }
        d = d.Fit();
        d.Name = "盘Ø56 记录（两段）";
        return d;
    }

    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static object? Get(object o, string n)
    {
        var t = o as Type ?? o.GetType(); object? inst = o is Type ? null : o;
        var f = t.GetField(n, All); if (f != null) return f.GetValue(inst);
        var p = t.GetProperty(n, All); if (p != null) return p.GetValue(inst);
        return null;
    }
    static bool Set(object o, string n, object v)
    {
        var t = o as Type ?? o.GetType(); object? inst = o is Type ? null : o;
        var f = t.GetField(n, All); if (f != null) { f.SetValue(inst, v); return true; }
        var p = t.GetProperty(n, All); if (p != null && p.SetMethod != null) { p.SetValue(inst, v); return true; }
        return false;
    }

    static string Dump(object o)
    {
        var parts = new List<string>();
        var t = o.GetType();
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name, StringComparer.Ordinal))
            parts.Add(f.Name + "=" + Val(f.FieldType, f.GetValue(o)));
        foreach (var pr in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(pr => pr.GetIndexParameters().Length == 0 && pr.CanRead && pr.CanWrite).OrderBy(pr => pr.Name, StringComparer.Ordinal))
        {
            object? v; try { v = pr.GetValue(o); } catch { v = "(异常)"; }
            parts.Add(pr.Name + "=" + Val(pr.PropertyType, v));
        }
        return string.Join("; ", parts.Where(x => x.Length < 400));
    }
    static string Val(Type ft, object? v)
    {
        if (v is null) return "null";
        if (v is double dd) return dd.ToString("R");
        if (v is string ss) return ss.Length > 60 ? "(长文本)" : ss;
        if (v is double[] da) return "[" + string.Join(",", da.Select(x => x.ToString("R"))) + "]";
        if (v is IEnumerable e && v is not string) { int n = 0; foreach (var _ in e) n++; return $"(集合 {n})"; }
        if (ft.IsPrimitive || ft.IsEnum || v is decimal) return v.ToString() ?? "";
        return "(" + ft.Name + ")";
    }

    static string A(double[] a) => "{" + string.Join(", ", a.Select(x => x.ToString("0.###"))) + "}";

    static string Stamped(string name)
    {
        var t = typeof(ZZO56ProbeTests).Assembly.GetType("PtOptimize.Tests.DeliverableOut");
        var m = t?.GetMethod("Stamped", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
        if (m != null) return (string)m.Invoke(null, new object[] { name })!;
        // 该树没有 DeliverableOut：照它的格式（名字_本次开跑于yyyy-MM-dd_HHmmss.ext，写到仓库根 deliverable/）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HANDOVER.md"))) dir = dir.Parent;
        string root = dir?.FullName ?? Directory.GetCurrentDirectory();
        string ext = Path.GetExtension(name), stem = Path.GetFileNameWithoutExtension(name);
        return Path.Combine(root, "deliverable", $"{stem}_本次开跑于{DateTime.Now:yyyy-MM-dd_HHmmss}{ext}");
    }

    [Trait("速度", "慢")]
    [Fact]
    public void ZZO56_冻结设计_导航网格一次场()
    {
        string tag = Environment.GetEnvironmentVariable("ZZO56_TAG") ?? "untagged";
        var variants = (Environment.GetEnvironmentVariable("ZZO56_VARIANTS") ?? "base").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new StringBuilder();
        var core = typeof(LineRunner).Assembly;
        sb.AppendLine($"ZZO56 探针　树 {tag}　开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　平台 Linux 镜像（Release）　同机并跑：是（另有 Core 驱动实施在编译与冒烟；耗时为争用下量得）");
        sb.AppendLine($"Core 程序集 {core.Location}");
        {
            var d0 = Disc56TwoSegs(); var p0 = new DesignInputs();
            sb.AppendLine("设计字段全表（DesignSpec，逐字段）：" + Dump(d0));
            sb.AppendLine("工艺参数全表（DesignInputs，逐属性）：" + Dump(p0));
        }
        foreach (var v in variants)
        {
            var d = Disc56TwoSegs();
            var p = new DesignInputs();
            string note = "";
            object? rules = null;
            bool skip = false;
            if (v == "SEG") { if (!Set(p, "SegCurrentContinuousRoot", false)) { skip = true; note = "该树无此开关 DesignInputs.SegCurrentContinuousRoot"; } else note = "改回：段电流收尾取中点（SegCurrentContinuousRoot=false）"; }
            var lc = d.BuildCase(p);
            if (v == "F3") { if (!Set(lc, "GateRevertFaceHeat", true)) { skip = true; note = "该树无此开关 LineCase.GateRevertFaceHeat"; } else note = "改回：热场发热用重构 J（GateRevertFaceHeat=true）"; }
            if (v == "F4") { if (!Set(lc, "ZoneByMaterialFraction", false)) { skip = true; note = "该树无此开关 LineCase.ZoneByMaterialFraction"; } else note = "改回：分区按格心整格（ZoneByMaterialFraction=false）"; }
            if (v.StartsWith("MR:"))
            {
                string prop = v.Substring(3);
                var mrT = core.GetType("PtOptimize.Core.MeshRules");
                var hook = typeof(LineRunner).GetField("ZzoRulesOverride", All);
                if (mrT == null) { skip = true; note = "该树无 MeshRules"; }
                else if (hook == null) { skip = true; note = "该工作树没有注入钩 ZzoRulesOverride"; }
                else
                {
                    rules = Activator.CreateInstance(mrT, true)!;
                    var pr = mrT.GetProperty(prop, All);
                    if (pr == null) { skip = true; note = $"该树 MeshRules 无 {prop}"; }
                    else { pr.SetValue(rules, false); hook.SetValue(null, rules); note = $"改回：MeshRules.{prop}=false（其余生产规则）"; }
                }
            }
            sb.AppendLine($"── 变体 {v}　{note}");
            if (skip) { sb.AppendLine($"ROW|{tag}|{v}|SKIP|{note}"); continue; }
            sb.AppendLine($"  设计：段 {d.SetpointC.Length}　控温 {A(d.SetpointC)}　段长 {A(d.SegLengthMm)}　管保温 {d.TubeInsulMm}　盘半径 {d.DiscRadiusMm}　舌半宽 {d.TabHalfWidthMm}　舌长 {d.TabLengthMm}　板厚 {A(d.TabThickMm)}　舌片厚 {A(d.TongueThickMm)}　舌保温 {A(d.TabInsulMm)}　环倍率 {A(d.RingMul)}　管壁 {Get(lc, "WallMm")}　圆盘保温 {Get(d, "DiscInsulMm") ?? "（该树无字段 DiscInsulMm）"}");
            sb.AppendLine($"  网格：hFine {lc.MeshFineMm} hCoarse {lc.MeshCoarseMm} fineR {lc.MeshFineRadiusMm} hInner {lc.MeshInnerMm} innerR {lc.MeshInnerRadiusMm}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LineResult r;
            try { r = LineRunner.Run(lc); }
            finally { typeof(LineRunner).GetField("ZzoRulesOverride", All)?.SetValue(null, null); }
            sw.Stop();
            sb.AppendLine($"  收敛 {r.Converged}　全判据 {r.AllOk}　MeshCells(片0) {r.MeshCells}　合计铂重 {r.TotalMassG:0.0} g（法兰 {r.FlangeMassG:0.0}）　用时 {sw.Elapsed.TotalSeconds:0} s　{(r.Ok ? "" : "✗ " + r.Message)}");
            for (int i = 0; i < r.Segments.Length; i++)
            {
                var s = r.Segments[i];
                sb.AppendLine($"  段{i + 1}：③ 增量温降 {s.FlangeDipK:0.000} K（A {s.FlangeDipAK:0.000} / B {s.FlangeDipBK:0.000}）　管根 {s.TRootAC:0.0}/{s.TRootBC:0.0} °C　基线 {s.BaseTRootAC:0.0}/{s.BaseTRootBC:0.0}　I {s.CurrentA:0.0} A");
            }
            double jpk = double.NegativeInfinity; string jpkAt = "";
            int cellsTot = 0;
            for (int j = 0; j < r.Flanges.Length; j++)
            {
                var f = r.Flanges[j];
                int cc = Get(f, "CellCount") is int ci ? ci : -1;
                string loc = "";
                if (Get(f, "Mesh") is ShellMesh m && Get(f, "JField") is double[] jf && jf.Length > 0)
                {
                    if (cc < 0) cc = m.CellCount;
                    int k = 0; for (int q = 1; q < jf.Length; q++) if (jf[q] > jf[k]) k = q;
                    var c = m.Centroid[k];
                    loc = $"JField 最大 {jf[k]:0.000} @ 格 {k}（x {c.X:0.00}, z {c.Z:0.00}, r {Math.Sqrt(c.X * c.X + c.Z * c.Z):0.00} mm，面积 {m.Area[k]:0.###} mm²）";
                }
                cellsTot += Math.Max(cc, 0);
                sb.AppendLine($"  片{j} {f.Name}：格数 {cc}　抽热 {f.QFromTubeW:0.000} W　铂重 {f.MassG:0.00} g　J峰 {f.JMaxAPerMm2:0.000} A/mm²　最高温 {f.TMaxC:0.0} °C　管根 {Get(f, "TRootC")}　{loc}");
                if (f.JMaxAPerMm2 > jpk) { jpk = f.JMaxAPerMm2; jpkAt = $"片{j}"; }
            }
            foreach (var c in r.Checks)
                sb.AppendLine($"  判据 {c.Name}：{c.Actual:0.000} / {c.Limit:0.###} {c.Unit}　{(c.Undetermined ? "无法判定" : c.Ok ? "✓" : "✗")}　{c.Where}");
            foreach (var n in r.Notes.Where(n => n.Contains("改回"))) sb.AppendLine($"  注：{n}");
            ConstraintOut? K(string pre) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(pre, StringComparison.Ordinal));
            string Fmt(ConstraintOut? c) => c is null ? "该树无此判据" : $"{c.Actual:0.000}/{c.Limit:0.###}{(c.Undetermined ? "?" : c.Ok ? "✓" : "✗")}";
            sb.AppendLine($"ROW|{tag}|{v}|cells0={r.MeshCells}|cellsAll={cellsTot}|dT={Fmt(K("③"))}|inflow={Fmt(K("②′"))}|hottest={Fmt(K("⑦"))}|root={Fmt(K("⑧"))}|disc={Fmt(K("②″"))}|jpeak={jpk:0.000}@{jpkAt}|s={sw.Elapsed.TotalSeconds:0}|mass={r.TotalMassG:0.0}|conv={r.Converged}");
        }
        string path = Stamped($"R48_Ø56冻结设计_网格逐版归因_{tag}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(sb.ToString());
        Console.WriteLine("WROTE " + path);
    }
}
