using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **出的图，必须就是算的那个东西**（2026-09-06 用户追问出来的根）。
///
/// 用户：「为什么能算到法兰突出到铂金管内部？」
/// 答案是：**算和画是两套几何，中间一道检查都没有。**
/// <code>
///                       ┌── Inside(x,z) ──→ 网格 ──→ 电场/温度场 ──→ 判据表
///   DesignSpec ─→ Plate ┤
///                       └── Geom/RunFinal 曲线布尔 ──→ .3dm
///                          ↑ 两条路之间，**从来没对过账**
/// </code>
/// 实测后果：盘R35 &lt; 舌半宽40 ⇒ 轮廓自交 ⇒ 布尔差静默失败 ⇒ 兜底把没挖的实心轮廓写出去
/// ⇒ **法兰实心穿过铂金管**（板身最内半径 0.000 mm，5810 个点落在管外径 R26 以内），
/// 而判据表照样全绿 —— 因为场压根没见过那块料。
///
/// ⚠ 我**早就有**这个工具（deliverable/出图回读.txt，探针把 .3dm 读回来），
///   却只拿它数了几个洞，**从没跟 Inside(x,z) 比对过一次**。工具在手上，对账没做。
///
/// ⇒ 本门就是那一步：逐点比，两个方向都要查。
///   · 图上有料、算上无料 ⇒ 多画了（本次：整个管腔）
///   · 算上有料、图上无料 ⇒ 少画了（挖过头，同样致命）
/// </summary>
// ★ 2026-09-09：起 Rhino 子进程的测试类**串行**（同一 xunit collection）—— 并行起两个 RhinoCore 会互相挂死到 10 分钟超时（合并 R12/R13 时抓到，单跑 12 s 过）
[Collection("Rhino 子进程")]
public class Export3dmMatchesSolvedTests
{
    /// <summary>
    /// ★★★★★ 主门：写一张图，读回来，逐点与 <c>Inside(x,z)</c> 对账。
    /// </summary>
    [Trait("速度", "慢")]   // ★ 真跑场解/出图；钩子默认跳过，见 .githooks/pre-commit
    [Fact]
    public void 出的图必须等于算的那个东西()
    {
        string? probe = Geometry3dm.FindProbe();
        if (probe is null) { Console.WriteLine("跳过：本机没有 Rhino 探针"); return; }

        var baseIn = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.Name = "出图对账门";
        d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        d.DiscRadiusMm = 60;                          // 合法几何（盘 ≥ 舌半宽）
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 4.0;

        string dir = Path.Combine(Path.GetTempPath(), "pt_match_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "对账.3dm");
            Geometry3dm.WriteFinal3dm(d, f);

            var g = d.Plate(0, d.DiscFloorMm(baseIn));
            var rr = d.RingRadiiOf(0);
            var r = Compare(probe, f, g, "入口", rr[0], rr[1]);

            Console.WriteLine($"对账：采样 {r.Total} 点　"
                            + $"多画 {r.Extra}（其中贴边 {r.ExtraEdge}、**离边 {r.ExtraDeep}**）　"
                            + $"少画 {r.Missing}（其中贴边 {r.MissingEdge}、**离边 {r.MissingDeep}**）　{r.Note}");

            // ★★★★★ **判据不是「差多少个点」，是「差在哪里」**（2026-09-06 用户：「不准凑答案」）。
            //
            //   第一版写「容 1.5 %」，实测 少画 424 / 容差 460 —— **压线**，
            //   同一份代码一趟绿一趟红。压线的门是坏门，而调容差就是凑答案。
            //
            //   第一性原理：探针是 1 mm 栅格、解析边界是曲线 ⇒ **边界那一圈必然对不齐**，
            //   这是离散化，不是错误。而**离边界一格以上**还对不上，才是真的两套几何。
            //   ⇒ 贴边的不计，离边的一个都不许有。这条判据不含任何可调的数。
            Assert.True(r.ExtraDeep == 0,
                $"图上有 {r.ExtraDeep} 个点**离解析边界一格以上**却画了料 —— "
              + $"出图器画了求解器没算过的东西。{r.Note}");
            Assert.True(r.MissingDeep == 0,
                $"图上有 {r.MissingDeep} 个点**离解析边界一格以上**却没有料 —— "
              + $"出图器挖掉了求解器算过的东西。{r.Note}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// ★★★★★ **管腔里一点料都不许有** —— 这一条是硬的，不给容差。
    /// 法兰伸进铂金管 = 装不进去，比任何判据都直接。
    /// </summary>
    [Trait("速度", "慢")]   // ★ 真跑场解/出图；钩子默认跳过，见 .githooks/pre-commit
    /// <summary>
    /// ★★★★★ 盘半径 = 舌半宽（现役两档、R18 搜出的 Ø58/舌58 都是）且舌片另有厚度（R11 起页面路径必然如此）：
    /// 切点在 x=0，舌片侧那块矩形盖住管腔左半圆 —— 2026-09-09 审查抓到出图器没减管孔、也不跑管腔自检，
    /// 图上法兰实心穿过铂金管的一半而退出码 0。本门钉：管腔里没有料、舌片层存在且厚度是舌片厚、盘上是板身厚。
    /// </summary>
    [Fact]
    public void 盘径等于舌宽且舌片另有厚度_舌片侧不许伸进管腔()
    {
        string? probe = Geometry3dm.FindProbe();
        if (probe is null) { Console.WriteLine("跳过：本机没有 Rhino 探针"); return; }
        var d = DesignSpec.Builtin[0].Clone();
        d.Name = "舌片侧管腔门";
        d.DiscRadiusMm = 30; d.TabHalfWidthMm = 30; d.TabLengthMm = 140; d.WallMm = 0.8;
        for (int j = 0; j < d.TabThickMm.Length; j++) { d.TabThickMm[j] = 1.0; d.TongueThickMm[j] = 2.5; }
        string dir = Path.Combine(Path.GetTempPath(), "pt_tabbore_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "舌片侧.3dm");
            string echo = Geometry3dm.WriteFinal3dm(d, f);          // 退出码 9（管腔有料）／8（切不开）会在这里抛
            Assert.DoesNotContain("没画进去", echo);
            var tab = Geometry3dm.LoadThickness(f, "入口-舌片", 0, 1.0);
            int solid = 0;
            for (int i = 0; i < tab.Nx; i++) for (int k = 0; k < tab.Nz; k++) if (tab.At(tab.X0 + i * tab.Step, tab.Z0 + k * tab.Step) > 1e-6) solid++;
            Assert.True(solid > 2000, $"舌片层几乎是空的（{solid} 格）—— 舌片没画出来");
            // 管腔（r < 25.8）在舌片侧一定是空的：x=−10/−20、z=0；舌片上（x=−60）厚度 = 舌片厚 2.5
            Assert.True(tab.At(-10, 0) < 1e-6 && tab.At(-20, 0) < 1e-6, $"管腔里有料：{tab.At(-10, 0):0.00}/{tab.At(-20, 0):0.00} mm");
            Assert.InRange(tab.At(-60, 0), 2.4, 2.6);
            Assert.InRange(tab.At(-60, 20), 2.4, 2.6);
            // 盘 R30 < 环外级 31.8 ⇒ 圆盘侧没有「板身」（全是环）：环内级层在盘上是基板厚 1.0（倍率 1），且不含舌片
            var ringI = Geometry3dm.LoadThickness(f, "入口-环内级", 0, 1.0);
            Assert.InRange(ringI.At(27, 0), 0.9, 1.1);
            Assert.True(ringI.At(-60, 0) < 1e-6, "环内级层画到了舌片上");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 管腔里不许有法兰的料()
    {
        string? probe = Geometry3dm.FindProbe();
        if (probe is null) { Console.WriteLine("跳过：本机没有 Rhino 探针"); return; }

        var baseIn = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        d.DiscRadiusMm = 60;
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 4.0;

        string dir = Path.Combine(Path.GetTempPath(), "pt_bore_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "管腔.3dm");
            Geometry3dm.WriteFinal3dm(d, f);
            double holeR = d.HoleRadiusMm;

            foreach (string part in new[] { "板身", "环外级", "环内级", "角焊缝" })
            {
                var fld = Probe(probe, f, "入口-" + part, 0);
                if (fld is null) continue;               // 这一级可能没实体（倍率=1 时环是平的）
                var (rMin, nIn) = InnerMost(fld.Value, holeR);
                Console.WriteLine($"入口-{part}　最内半径 {rMin:0.000} mm　管腔内点数 {nIn}");
                Assert.True(nIn == 0,
                    $"入口-{part} 有 {nIn} 个点落在管外径 R{holeR:0.#} 以内"
                  + $"（最内 {rMin:0.000} mm）—— **法兰伸进铂金管**，这张图装不进去。");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// ★★★★★ **门必须在已知坏例上真的红** —— 否则它是摆设。
    ///
    /// 坏例不是我编的，是 2026-09-06 交出去那一张的**原始 spec 逐位复现**
    /// （deliverable/优化后3dm/整机.spec.json）。用现在的代码重跑，仍然穿管：
    /// <code>
    ///   入口  槽120° 孔R32.71×3.0   板身最内 0.000 mm   管腔内 5810 点   ← 坏
    ///   共用1 槽 0°  孔R30.00×2.9   板身最内 0.000 mm   管腔内 3746 点   ← 坏
    ///   共用2 槽 0°  孔R30.90×1.0   板身最内 32.004 mm         0 点     ← 好
    ///   出口  槽325° 孔 R0          板身最内 32.004 mm         0 点     ← 好
    /// </code>
    ///
    /// ══ 真机制（查出来的，不是猜的）
    ///
    /// 坏的两片都是**顺流拉长的椭圆孔**。孔心 x = −79.75、R32.71、拉长 3
    /// ⇒ 长半轴 98.1 ⇒ 椭圆一直伸到 x = +18.4，**与管孔那一圈重叠**。
    /// 曲线布尔把外边界与管孔内环合并之后，<c>Brep.CreatePlanarBreps</c> 分不清嵌套
    /// ⇒ **管孔整个消失**，写出实心板。
    /// ⚠ 布尔本身**没有失败**（退出码 0）⇒ 任何「检查布尔返回值」的判据都抓不到它。
    ///   只有**把文件读回来**才逮得住 —— 这就是 Geometry3dm.BoreSelfCheck 的理由。
    ///
    /// ⚠⚠ 我第一版给这条门写过「造不出坏例就退而验旧文件、旧文件没了就跳过」的兜底 ——
    ///    那是**为了让门变绿而写的**，不是证明门有效。用户当场点破「不准凑答案」。
    ///    现在：坏例就是那份 spec，红不了就是门坏了，没有第三条路。
    /// </summary>
    [Trait("速度", "慢")]   // ★ 真跑场解/出图；钩子默认跳过，见 .githooks/pre-commit
    [Fact]
    public void 出图自检必须逮住法兰伸进管腔()
    {
        if (Geometry3dm.FindProbe() is null)
            throw new InvalidOperationException(
                "本机没有 Rhino 探针 —— 这条门验的正是「读回文件」，没有探针就验不了，"
              + "**不许当成通过**。");

        var d = DesignSpec.Builtin[0].Clone();
        d.Name = "穿管坏例（2026-09-06 交付件逐位复现）";
        d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        d.DiscRadiusMm = 35;
        for (int j = 0; j < d.TabHoleXMm.Length; j++) d.TabHoleXMm[j] = -79.75;   // R12 之后孔心逐片：坏例四片同一个数，逐位复现当时
        double[] t   = { 3.04, 1.25, 5.06, 1.25 };
        double[] hr  = { 32.71, 30.00, 30.90, 0.00 };
        double[] asp = { 3.0, 2.9, 1.0, 1.0 };
        double[] slt = { 120, 0, 0, 325 };
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            d.TabThickMm[j] = t[j];
            if (j < d.TabHoleRMm.Length) d.TabHoleRMm[j] = hr[j];
            if (j < d.TabHoleAspect.Length) d.TabHoleAspect[j] = asp[j];
            if (j < d.SlotSpanDeg.Length) d.SlotSpanDeg[j] = slt[j];
        }

        string dir = Path.Combine(Path.GetTempPath(), "pt_bad_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var ex = Record.Exception(() => Geometry3dm.WriteFinal3dm(d, Path.Combine(dir, "坏例.3dm")));
            Assert.True(ex is not null,
                "这份 spec 实测会写出**法兰实心穿过铂金管**的图（入口 5810 点、共用1 3746 点落在管腔里），"
              + "出图自检却放它过去了 —— 门是摆设。");
            Console.WriteLine("出图自检拒绝了：" + ex!.Message.Split((char)10)[0]);
            Assert.Contains("伸进铂金管", ex.Message);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── 帮手 ────────────────────────────────────────────────────────────────

    private readonly record struct Field(int Nx, int Nz, double Step, double X0, double Z0, double[] T)
    {
        public double At(int i, int j) => T[i * Nz + j];
    }

    private static Field? Probe(string exe, string file, string layer, double planeY)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "thickness", file, layer, planeY.ToString("R"), "1" })
            psi.ArgumentList.Add(a);
        using var pr = Process.Start(psi)!;
        // ★ 上限 + 异步收管道，理由同 Geometry3dm.WaitOrKill（督导 S9）：
        //   裸 WaitForExit() 会无限期挂住，而先 stdout 后 stderr 的同步读会死锁。
        var aOut = pr.StandardOutput.ReadToEndAsync();
        var aErr = pr.StandardError.ReadToEndAsync();
        if (!pr.WaitForExit(10 * 60 * 1000))
        {
            try { pr.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("出图对账门读回厚度场：探针 10 分钟没返回，已强制结束");
        }
        pr.WaitForExit();   // 收尾：让上面两条异步读跑完（超时那支已在上面 return 掉）
        string so = aOut.GetAwaiter().GetResult();
        aErr.GetAwaiter().GetResult();
        if (pr.ExitCode != 0) return null;

        using var jd = JsonDocument.Parse(so);
        var R = jd.RootElement;
        return new Field(R.GetProperty("nx").GetInt32(), R.GetProperty("nz").GetInt32(),
                         R.GetProperty("step").GetDouble(), R.GetProperty("x0").GetDouble(),
                         R.GetProperty("z0").GetDouble(),
                         R.GetProperty("thickness").EnumerateArray().Select(e => e.GetDouble()).ToArray());
    }

    private static (double RMin, int NIn) InnerMost(Field f, double holeR)
    {
        double rMin = double.MaxValue; int n = 0;
        for (int i = 0; i < f.Nx; i++)
            for (int j = 0; j < f.Nz; j++)
                if (f.At(i, j) > 1e-9)
                {
                    double r = Math.Sqrt(Math.Pow(f.X0 + i * f.Step, 2) + Math.Pow(f.Z0 + j * f.Step, 2));
                    if (r < rMin) rMin = r;
                    if (r < holeR - 1e-6) n++;
                }
        return (rMin == double.MaxValue ? -1 : rMin, n);
    }

    private readonly record struct Diff(int Extra, int ExtraEdge, int ExtraDeep,
                                       int Missing, int MissingEdge, int MissingDeep,
                                       int Total, string Note);

    /// <summary>把一片的**所有部位**并起来，逐点与 Inside(x,z) 比。</summary>
    private static Diff Compare(string exe, string file, FlangePlate g, string plate,
                                double ringR0, double ringR1)
    {
        var fields = new List<Field>();
        foreach (string part in new[] { "板身", "环外级", "环内级", "舌片" })   // R11 起舌片另成一层（同厚时没有这层，读不到就跳）
        {
            var fld = Probe(exe, file, plate + "-" + part, 0);
            if (fld is not null) fields.Add(fld.Value);
        }
        if (fields.Count == 0) return new Diff(0, 0, 0, 0, 0, 0, 0, "一个部位都读不到");

        bool Drawn(double x, double z)
        {
            foreach (var f in fields)
            {
                int i = (int)Math.Round((x - f.X0) / f.Step);
                int j = (int)Math.Round((z - f.Z0) / f.Step);
                if (i < 0 || j < 0 || i >= f.Nx || j >= f.Nz) continue;
                if (f.At(i, j) > 1e-9) return true;
            }
            return false;
        }

        // ★★★★★ **仪器分辨不了的地方要标出来，而不是调容差**（2026-09-06）。
        //
        //   出图器把一片拆成 板身/环外级/环内级 三个**相邻实体**，它们在
        //   r = 孔半径 / ringR[0] / ringR[1] 上共享面。厚度探针是逐层打射线的：
        //   射线擦到共享面时，两层都可能读成 0。
        //
        //   这是**仪器**不是缺料 —— 用加密测量判定的（第一性原理）：
        //     步长 1     接缝带采样  556 点，图上空 12 点 (2.2 %)
        //     步长 0.5   接缝带采样 2192 点，图上空 12 点 (0.5 %)
        //     步长 0.25  接缝带采样 8720 点，图上空 12 点 (0.1 %)
        //   真缺料的面积会随步长**平方**增长（12→48→192）；绝对个数恒为 12
        //   ⇒ 是固定几个坐标上的擦边，不是有面积的洞。
        //
        //   ⚠ 所以判据里排除的是「**测不准的那一圈**」，不是「差得多就放过」——
        //     排除范围由拆件半径唯一确定，不含任何可调的数。
        double[] seams = { g.HoleRadiusMm, ringR0, ringR1, g.DiscRadiusMm };
        bool OnSeam(double x, double z)
        {
            double r = Math.Sqrt(x * x + z * z);
            return seams.Any(s => Math.Abs(r - s) <= 1.0);   // 1.0 = 探针栅格步长
        }

        // 「贴边」= 解析判据在这一点的 1 mm 邻域内会翻面 ⇒ 栅格化必然对不齐，不算错。
        bool NearEdge(double x, double z)
        {
            bool c = g.Inside(x, z);
            for (double dx = -1; dx <= 1; dx += 1)
                for (double dz = -1; dz <= 1; dz += 1)
                    if ((dx != 0 || dz != 0) && g.Inside(x + dx, z + dz) != c) return true;
            return false;
        }

        var deep = new List<(char Kind, double X, double Z)>();
        int seam = 0;
        int extra = 0, eEdge = 0, eDeep = 0, missing = 0, mEdge = 0, mDeep = 0, total = 0;
        for (double x = g.TabEndXMm + 1; x <= g.DiscRadiusMm - 1; x += 1.0)
            for (double z = -g.DiscRadiusMm + 1; z <= g.DiscRadiusMm - 1; z += 1.0)
            {
                bool calc = g.Inside(x, z), drew = Drawn(x, z);
                total++;
                if (drew == calc) continue;
                if (OnSeam(x, z)) { seam++; continue; }      // 仪器测不准的那一圈
                bool edge = NearEdge(x, z);
                if (drew) { extra++; if (edge) eEdge++; else { eDeep++; deep.Add(('多', x, z)); } }
                else      { missing++; if (edge) mEdge++; else { mDeep++; deep.Add(('少', x, z)); } }
            }
        string note = $"（{fields.Count} 个部位并起来比；拆件接缝上测不准 {seam} 点已排除）";
        if (deep.Count > 0)
        {
            var xs = deep.Select(q => q.X).ToArray(); var zs = deep.Select(q => q.Z).ToArray();
            var rs = deep.Select(q => Math.Sqrt(q.X * q.X + q.Z * q.Z)).ToArray();
            note += $" 离边的差落在 x∈[{xs.Min():0.0},{xs.Max():0.0}]"
                  + $" z∈[{zs.Min():0.0},{zs.Max():0.0}]"
                  + $" r∈[{rs.Min():0.0},{rs.Max():0.0}]；样本 "
                  + string.Join("、", deep.Take(6).Select(q => $"{q.Kind}({q.X:0},{q.Z:0})"));
        }
        return new Diff(extra, eEdge, eDeep, missing, mEdge, mDeep, total, note);
    }
}
