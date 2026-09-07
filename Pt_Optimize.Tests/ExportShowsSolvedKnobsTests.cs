using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **求解器选的，出图上要看得见**（2026-09-05，用户「我要的是 APP 自己能出结果图」）。
///
/// 链路上有三处断口，全是「赋了值 ≠ 用它的人读得到」这一族：
/// <code>
///   ① 求解器补不上就收摊       → ComboAccumulateTests
///   ② 解出来的槽/孔/r₁/r₂ 不回控件 → 本文件「九根旋钮都要回控件」
///   ③ APP 的出图器根本画不出槽和孔 → 本文件「出图真的切出槽与孔」
/// </code>
///
/// ③ 最隐蔽：<c>WriteFinal3dm</c> 的 spec JSON 里**只有 t 和 ring**，
/// 求解器解出 120° 槽 + 36 mm 孔，导出的 .3dm 上一个都没有 ——
/// 两边各自都自洽，只有把图读回来才看得出。
/// </summary>
public class ExportShowsSolvedKnobsTests
{
    // ══════════════════════════════════════════════════════════════════════
    //  ② 九根旋钮都要回控件
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ★★★★ <c>AdoptSolvedDesign</c> 是「解 → 页面」的**唯一入口**。
    /// 求解器每有一根旋钮，它就必须回填对应的那个字段；
    /// 漏一根 = 页面上看不到、导出时被默认值顶掉。
    ///
    /// ⚠ 本门钉的是**对应关系**，不是措辞：下面这张表少一项就红，
    ///   新增旋钮却忘了加回填也红（表的条数与 Knob 的个数对不上）。
    /// </summary>
    [Fact]
    public void 九根旋钮都要回控件()
    {
        // 旋钮 → 它在 DesignSpec 里的字段名
        var map = new Dictionary<Solver.Knob, string>
        {
            [Solver.Knob.Thick]         = "TabThickMm",
            [Solver.Knob.Insul]         = "TabInsulMm",
            [Solver.Knob.Ring]          = "RingMul",
            [Solver.Knob.RingT2]        = "RingMul2",
            [Solver.Knob.RingR1]        = "RingW1Mm",
            [Solver.Knob.RingR2]        = "RingW2Mm",
            [Solver.Knob.SlotSpan]      = "SlotSpanDeg",
            [Solver.Knob.TabHoleR]      = "TabHoleRMm",
            [Solver.Knob.TabHoleAspect] = "TabHoleAspect",
        };

        var all = Enum.GetValues<Solver.Knob>();
        Assert.True(map.Count == all.Length,
            $"求解器有 {all.Length} 根旋钮，本门只登记了 {map.Count} 根 —— "
          + "新增旋钮时必须同时补『回控件』那一步，否则解出来的值到不了出图。"
          + "缺：" + string.Join("、", all.Where(k => !map.ContainsKey(k)).Select(Solver.KnobName)));

        string body = AdoptBody();
        var missing = map.Where(kv => !body.Contains("d." + kv.Value, StringComparison.Ordinal))
                         .Select(kv => Solver.KnobName(kv.Key) + "（d." + kv.Value + "）")
                         .ToList();
        Assert.True(missing.Count == 0,
            "AdoptSolvedDesign 没有把这几根旋钮写回控件：" + string.Join("、", missing)
          + "。后果是静默的 —— 页面读回默认值，导出的图上没有它们。");
    }

    /// <summary>AdoptSolvedDesign 的方法体（到下一个成员声明为止）。</summary>
    private static string AdoptBody()
    {
        string s = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
        int a = s.IndexOf("private void AdoptSolvedDesign(", StringComparison.Ordinal);
        Assert.True(a > 0, "找不到 AdoptSolvedDesign —— 「解 → 页面」的唯一入口改名了？");
        int b = s.IndexOf("        PushFlow();", a, StringComparison.Ordinal);
        Assert.True(b > a, "AdoptSolvedDesign 里找不到 PushFlow —— 结构变了，本门要跟着改");
        return s[a..b];
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ③ 出图真的切出槽与孔（写 → 读 往返）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ★★★★★ **写出去再读回来**：APP 自己的出图器（<c>WriteFinal3dm</c>，
    /// 就是「导出本页 3DM」按钮走的那一个）写一张带槽带孔的图，
    /// 再用厚度探针读回来，数一数内部空洞。
    ///
    /// 期望：管孔、圆盘背侧减重槽、舌板椭圆孔 —— **三个**，一个都不能少。
    /// 椭圆孔的面积还要对得上 π·R²·拉长比（形状对不对，只有面积说了算）。
    ///
    /// ⚠ 需要本机 Rhino 8。没有就跳过 —— 但**不许静默通过**（会在输出里说清楚）。
    /// </summary>
    [Fact]
    public void 出图真的切出槽与孔()
    {
        string? probe = Geometry3dm.FindProbe();
        if (probe is null) { Console.WriteLine("跳过：本机没有 Pt_Optimize.Geom.exe（需 Rhino 8）"); return; }

        var d = DesignSpec.Builtin[0].Clone();
        d.Name = "出图往返门";
        d.DiscRadiusMm = 60; d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 4.0;
        // 只给**第 0 片**开槽开孔 —— 其余三片留空，正好顺带验「没开的片不许凭空多洞」
        d.SlotSpanDeg[0] = 120;
        d.TabHoleRMm[0] = 20;
        d.TabHoleAspect[0] = 2.0;

        string dir = Path.Combine(Path.GetTempPath(), "pt_export_gate_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "往返.3dm");
            Geometry3dm.WriteFinal3dm(d, f);
            Assert.True(File.Exists(f), "出图器没写出文件");

            // 片 0 在 y=0，片 1 在 y=segLen —— 探针按中面选组
            var holes0 = HolesOf(probe, f, "入口", planeY: 0);
            var holes1 = HolesOf(probe, f, "共用1", planeY: 300);

            Assert.True(holes0.Count >= 3,
                $"片0 只读到 {holes0.Count} 个内部空洞（面积 "
              + string.Join("、", holes0.Select(h => h.ToString("0"))) + " mm²）—— "
              + "应有 管孔 + 圆盘背侧减重槽 + 舌板椭圆孔 三个。"
              + "少了就是出图器没把求解器选的旋钮画出来。");

            // 椭圆孔：π·R²·拉长比 = π·400·2 = 2513 mm²
            double want = Math.PI * 20 * 20 * 2.0;
            Assert.True(holes0.Any(h => Math.Abs(h - want) / want < 0.06),
                $"找不到面积 ≈ {want:0} mm² 的椭圆孔（读到 "
              + string.Join("、", holes0.Select(h => h.ToString("0"))) + "）—— "
              + "孔画出来了但**形状不对**：拉长比没跟着走，或画成了正圆。");

            // 没开槽开孔的片：只该有管孔一个
            Assert.True(holes1.Count <= 1,
                $"片1 没开槽没开孔，却读到 {holes1.Count} 个内部空洞 —— "
              + "槽/孔被画到了不该有的片上（逐片参数串了）。");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>跑一次厚度探针，返回各内部空洞的面积 mm²（降序，忽略 &lt; 20 mm² 的擦边噪点）。</summary>
    private static List<double> HolesOf(string probe, string file, string layer, double planeY)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(probe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add("thickness");
        psi.ArgumentList.Add(file);
        psi.ArgumentList.Add(layer);
        psi.ArgumentList.Add(planeY.ToString("R"));
        psi.ArgumentList.Add("1");
        using var pr = System.Diagnostics.Process.Start(psi)!;
        // ★ 上限 + 异步收管道，理由同 Geometry3dm.WaitOrKill（督导 S9）：
        //   裸 WaitForExit() 会无限期挂住，而先 stdout 后 stderr 的同步读会死锁。
        var aOut = pr.StandardOutput.ReadToEndAsync();
        var aErr = pr.StandardError.ReadToEndAsync();
        if (!pr.WaitForExit(10 * 60 * 1000))
        {
            try { pr.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("出图带旋钮门读回厚度场：探针 10 分钟没返回，已强制结束");
        }
        pr.WaitForExit();   // 收尾：让上面两条异步读跑完（超时那支已在上面 return 掉）
        string so = aOut.GetAwaiter().GetResult();
        string se = aErr.GetAwaiter().GetResult();
        Assert.True(pr.ExitCode == 0, $"探针退出码 {pr.ExitCode}：{se}");

        using var jd = JsonDocument.Parse(so);
        var R = jd.RootElement;
        int nx = R.GetProperty("nx").GetInt32(), nz = R.GetProperty("nz").GetInt32();
        double step = R.GetProperty("step").GetDouble();
        var T = R.GetProperty("thickness").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        double At(int i, int j) => T[i * nz + j];          // 列优先，与写入端一致

        // 内部空洞 = 空点，且四个方向上都还有料
        var hs = new HashSet<(int, int)>();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                if (At(i, j) > 1e-9) continue;
                bool L = false, Rt = false, D = false, U = false;
                for (int a = 0; a < i && !L; a++) L = At(a, j) > 1e-9;
                for (int a = i + 1; a < nx && !Rt; a++) Rt = At(a, j) > 1e-9;
                for (int b = 0; b < j && !D; b++) D = At(i, b) > 1e-9;
                for (int b = j + 1; b < nz && !U; b++) U = At(i, b) > 1e-9;
                if (L && Rt && D && U) hs.Add((i, j));
            }

        // 连通分量
        var seen = new HashSet<(int, int)>();
        var areas = new List<double>();
        foreach (var p0 in hs)
        {
            if (!seen.Add(p0)) continue;
            var st = new Stack<(int, int)>(); st.Push(p0); int n = 0;
            while (st.Count > 0)
            {
                var (x, z) = st.Pop(); n++;
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var q = (x + dx, z + dz);
                    if (hs.Contains(q) && seen.Add(q)) st.Push(q);
                }
            }
            double a = n * step * step;
            if (a >= 20) areas.Add(a);        // 射线擦边会留下 1 mm² 的孤点，不是洞
        }
        areas.Sort((a, b) => b.CompareTo(a));
        return areas;
    }
}
