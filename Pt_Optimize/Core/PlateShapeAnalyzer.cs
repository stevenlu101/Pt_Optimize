using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// **从 .3dm 的厚度场反推出法兰的几何变数** —— 把「一张图」变成「一组可优化的参数」。
///
/// 为什么需要：用户画的法兰是任意形状（阶梯、开槽、异形轮廓），
/// 程序若只会整体缩放厚度，就只有 1 个自由度，攻不了局部过热那条约束。
/// 先把图纸解析成参数（各级半径、各级厚度、槽数、槽宽、舌片尺寸…），
/// 这些参数才能成为优化的自由度。
///
/// 依据：厚度场 t(x,z) 已含全部信息 —— **t=0 表示无材料**，
/// 所以轮廓、管孔、开槽三者用同一个量表达（§2 的设计）。
/// 本类只做「量」，不做任何判定。
/// </summary>
public static class PlateShapeAnalyzer
{
    public sealed class Level
    {
        /// <summary>该级的厚度 mm</summary>
        public double ThicknessMm;
        /// <summary>该级材料所处的半径范围 mm</summary>
        public double RInnerMm, ROuterMm;
        /// <summary>该级占的面积 mm² 与体积 mm³</summary>
        public double AreaMm2, VolumeMm3;
        public double MassG => VolumeMm3 * Materials.PtDensity * 1e-6;
    }

    public sealed class Slots
    {
        public int Count;
        /// <summary>每个槽的角宽 °（均值）</summary>
        public double WidthDeg;
        /// <summary>槽所在的半径范围 mm</summary>
        public double RInnerMm, ROuterMm;
        public double AreaMm2;
        public bool Found => Count > 0;
    }

    public sealed class Shape
    {
        public double HoleRadiusMm;
        /// <summary>圆盘外半径 mm（背离舌片一侧材料终止处）</summary>
        public double DiscRadiusMm;
        /// <summary>舌片末端 X mm（负值）与该处半宽 mm；无舌片时为 NaN</summary>
        public double TabEndXMm = double.NaN, TabEndHalfWidthMm = double.NaN;
        public List<Level> Levels = new();
        public Slots Slot = new();
        public double NetAreaMm2, VolumeMm3;
        /// <summary>板中心（管孔形心）在图纸坐标里的位置 mm —— 不假设它在原点</summary>
        public double CenterXMm, CenterZMm;
        public double MassG => VolumeMm3 * Materials.PtDensity * 1e-6;
        public readonly List<string> Notes = new();
    }

    /// <summary>
    /// 找出**被材料包围的空腔**（管孔与开槽），并给出板的中心。
    ///
    /// 做法：从图幅边界做连通域填充标出「外部空白」，剩下的 t=0 就是内部空腔。
    /// 最大的那个是管孔，其余是开槽 —— 这样**孔与槽天然分开**，
    /// 不必靠环向扫描去猜（那种猜法在多片并排或板不居中时会给出假槽）。
    /// </summary>
    private static (List<List<int>> voids, int holeIdx) FindVoids(ThicknessField f)
    {
        int nx = f.Nx, nz = f.Nz, n = nx * nz;   // ★ 索引 x 优先：id = ix*nz + iz（与 ThicknessField.At 一致）
        var outside = new bool[n];
        var stack = new Stack<int>();
        void Push(int ix, int iz)
        {
            if (ix < 0 || ix >= nx || iz < 0 || iz >= nz) return;
            int id = ix * nz + iz;
            if (outside[id] || f.T[id] > 1e-6) return;
            outside[id] = true; stack.Push(id);
        }
        for (int ix = 0; ix < nx; ix++) { Push(ix, 0); Push(ix, nz - 1); }
        for (int iz = 0; iz < nz; iz++) { Push(0, iz); Push(nx - 1, iz); }
        while (stack.Count > 0)
        {
            int id = stack.Pop(); int ix = id / nz, iz = id % nz;
            Push(ix + 1, iz); Push(ix - 1, iz); Push(ix, iz + 1); Push(ix, iz - 1);
        }

        var seen = new bool[n];
        var voids = new List<List<int>>();
        for (int id0 = 0; id0 < n; id0++)
        {
            if (f.T[id0] > 1e-6 || outside[id0] || seen[id0]) continue;
            var comp = new List<int>();
            var st = new Stack<int>(); st.Push(id0); seen[id0] = true;
            while (st.Count > 0)
            {
                int id = st.Pop(); comp.Add(id);
                int ix = id / nz, iz = id % nz;
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int jx = ix + dx, jz = iz + dz;
                    if (jx < 0 || jx >= nx || jz < 0 || jz >= nz) continue;
                    int jd = jx * nz + jz;
                    if (seen[jd] || outside[jd] || f.T[jd] > 1e-6) continue;
                    seen[jd] = true; st.Push(jd);
                }
            }
            voids.Add(comp);
        }
        int hole = -1;
        for (int i = 0; i < voids.Count; i++)
            if (hole < 0 || voids[i].Count > voids[hole].Count) hole = i;
        return (voids, hole);
    }

    /// <summary>
    /// 解析厚度场。<paramref name="levelTolMm"/> 是厚度分级的容差 ——
    /// 量出来的厚度总带射线离散噪声，差别小于它的并作一级。
    ///
    /// ⚠ 板的中心由**管孔的形心**定，不假设它在原点：
    /// 用户的图未必居中，一个文件里也可能放了不止一片。
    /// </summary>
    public static Shape Analyze(ThicknessField f, double levelTolMm = 0.05)
    {
        var sh = new Shape();
        double step = f.Step, a = step * step;

        // ── ① 厚度分级：对非零厚度做直方图聚类
        var vals = new List<double>();
        for (int i = 0; i < f.T.Length; i++) if (f.T[i] > 1e-6) vals.Add(f.T[i]);
        if (vals.Count == 0) { sh.Notes.Add("该图层没有材料"); return sh; }
        vals.Sort();

        var groups = new List<List<double>> { new() { vals[0] } };
        foreach (var v in vals.Skip(1))
        {
            if (v - groups[^1][^1] <= levelTolMm) groups[^1].Add(v);
            else groups.Add(new List<double> { v });
        }

        // ── ② 逐格归级，同时统计半径范围与面积
        var lv = groups.Select(g => new Level
        {
            ThicknessMm = g.Average(),
            RInnerMm = double.MaxValue,
            ROuterMm = 0
        }).ToList();

        // ── 先定中心与管孔：孔 = 被材料包围的最大空腔
        var (voids, holeIdx) = FindVoids(f);
        double cx = 0, cz = 0;
        if (holeIdx >= 0)
        {
            foreach (var id in voids[holeIdx])
            { cx += f.X0 + (id / f.Nz) * step; cz += f.Z0 + (id % f.Nz) * step; }
            cx /= voids[holeIdx].Count; cz /= voids[holeIdx].Count;
            sh.HoleRadiusMm = Math.Sqrt(voids[holeIdx].Count * a / Math.PI);   // 等面积圆半径
        }
        else sh.Notes.Add("未找到被材料包围的管孔 —— 中心退回原点，半径类结果可能不准");
        sh.CenterXMm = cx; sh.CenterZMm = cz;

        double holeProbe = double.MaxValue;      // 有材料的最小半径
        double maxRPlus = 0;                     // +x 侧材料最远半径 ⇒ 圆盘外半径
        double minX = 0;                         // 材料最小 x ⇒ 舌片末端
        for (int iz = 0; iz < f.Nz; iz++)
            for (int ix = 0; ix < f.Nx; ix++)
            {
                double t = f.T[ix * f.Nz + iz];
                if (t <= 1e-6) continue;
                double x = f.X0 + ix * step - cx, z = f.Z0 + iz * step - cz;
                double r = Math.Sqrt(x * x + z * z);

                int k = 0;
                for (int q = 0; q < lv.Count; q++)
                    if (Math.Abs(t - lv[q].ThicknessMm) < Math.Abs(t - lv[k].ThicknessMm)) k = q;
                lv[k].RInnerMm = Math.Min(lv[k].RInnerMm, r);
                lv[k].ROuterMm = Math.Max(lv[k].ROuterMm, r);
                lv[k].AreaMm2 += a;
                lv[k].VolumeMm3 += a * t;

                holeProbe = Math.Min(holeProbe, r);
                if (x > 0) maxRPlus = Math.Max(maxRPlus, r);
                minX = Math.Min(minX, x);
                sh.NetAreaMm2 += a;
                sh.VolumeMm3 += a * t;
            }

        sh.Levels = lv.Where(l => l.AreaMm2 > 4 * a).OrderBy(l => l.RInnerMm).ToList();
        if (holeIdx < 0) sh.HoleRadiusMm = holeProbe;
        sh.DiscRadiusMm = maxRPlus;

        // ── ③ 舌片：材料最小 x 处的半宽
        if (minX < -sh.DiscRadiusMm * 1.05)
        {
            sh.TabEndXMm = minX;
            int ixEnd = (int)Math.Round((minX + cx - f.X0) / step);
            double half = 0;
            for (int iz = 0; iz < f.Nz; iz++)
                if (ixEnd >= 0 && ixEnd < f.Nx && f.T[ixEnd * f.Nz + iz] > 1e-6)
                    half = Math.Max(half, Math.Abs(f.Z0 + iz * step - cz));
            sh.TabEndHalfWidthMm = half;
        }

        // ── ④ 开槽 = 除管孔外的其余内部空腔（面积过小的当噪点丢掉）
        var slots = new List<List<int>>();
        for (int i = 0; i < voids.Count; i++)
            if (i != holeIdx && voids[i].Count * a > 2.0) slots.Add(voids[i]);
        if (slots.Count > 0)
        {
            sh.Slot.Count = slots.Count;
            double rin = double.MaxValue, rout = 0, wsum = 0;
            foreach (var comp in slots)
            {
                double amin = double.MaxValue, amax = -double.MaxValue;
                foreach (var id in comp)
                {
                    double x = f.X0 + (id / f.Nz) * step - cx, z = f.Z0 + (id % f.Nz) * step - cz;
                    double r = Math.Sqrt(x * x + z * z), th = Math.Atan2(z, x);
                    rin = Math.Min(rin, r); rout = Math.Max(rout, r);
                    amin = Math.Min(amin, th); amax = Math.Max(amax, th);
                }
                wsum += (amax - amin) * 180.0 / Math.PI;
            }
            sh.Slot.RInnerMm = rin; sh.Slot.ROuterMm = rout;
            sh.Slot.WidthDeg = wsum / slots.Count;
            sh.Slot.AreaMm2 = slots.Sum(c => c.Count) * a;
        }
        return sh;
    }


    /// <summary>把解析结果排成人能读的表</summary>
    public static string Format(Shape s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== 法兰几何变数（由 .3dm 厚度场反推）===");
        sb.AppendLine($"管孔半径      R{s.HoleRadiusMm:0.00} mm   （Ø{2 * s.HoleRadiusMm:0.0}）");
        sb.AppendLine($"圆盘外半径    R{s.DiscRadiusMm:0.00} mm   （Ø{2 * s.DiscRadiusMm:0.0}）");
        if (!double.IsNaN(s.TabEndXMm))
            sb.AppendLine($"舌片          长度 {-s.TabEndXMm:0.0} mm　末端半宽 {s.TabEndHalfWidthMm:0.0} mm");
        else
            sb.AppendLine("舌片          未检出（可能是纯环形片）");

        sb.AppendLine();
        sb.AppendLine($"厚度分级 {s.Levels.Count} 级：");
        sb.AppendLine($"  {"级",4}{"厚度 mm",10}{"半径范围 mm",16}{"面积 mm²",11}{"铂重 g",10}");
        for (int i = 0; i < s.Levels.Count; i++)
        {
            var l = s.Levels[i];
            sb.AppendLine($"  {i + 1,4}{l.ThicknessMm,10:0.000}" +
                          $"{$"{l.RInnerMm:0.0} – {l.ROuterMm:0.0}",16}{l.AreaMm2,11:0}{l.MassG,10:0.0}");
        }

        sb.AppendLine();
        if (s.Slot.Found)
            sb.AppendLine($"开槽          {s.Slot.Count} 个　角宽约 {s.Slot.WidthDeg:0.0}°　" +
                          $"径向 R{s.Slot.RInnerMm:0.0} – R{s.Slot.ROuterMm:0.0} mm");
        else
            sb.AppendLine("开槽          未检出");

        sb.AppendLine();
        sb.AppendLine($"净面积 {s.NetAreaMm2:0} mm²　体积 {s.VolumeMm3:0} mm³　**单片铂重 {s.MassG:0.0} g**");
        foreach (var n in s.Notes) sb.AppendLine("⚠ " + n);
        sb.AppendLine();
        sb.AppendLine("以上每一项都可以作为优化的自由度 —— 厚度各级可独立调，");
        sb.AppendLine("半径与槽的尺寸改动需回 Rhino 出图（厚度改动本程序可直接出图）。");
        return sb.ToString();
    }
}
