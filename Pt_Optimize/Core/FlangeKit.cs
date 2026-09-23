using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PtOptimize.Core;

/// <summary>
/// ★ R46（2026-09-12，用户原话：「APP有保温与铜排的计算结果吗? 计算出的法兰要含有配套答案(如果没有，请补齐)」）：
/// 解出来的法兰**逐片**配什么铜排、包多厚保温、焊多大焊脚 —— 这些数整线解里本来就算了
/// （<see cref="FlangeOut.BusSectionForCurrentMm2"/>／<see cref="FlangeOut.BusSectionForHeatMm2"/>／
/// <see cref="FlangeOut.QClampW"/>／<see cref="FlangeOut.TTabEndC"/>；舌保温是求解器的旋钮），
/// 只是散在结果对象与设计记录里，工程师在界面上看不到。这里把它们**按片对齐成一张配套清单**，
/// 「③ 结果与出图」页一张表、输出框一段文字、安装报告一节，三处同一个来源。
///
/// 口径（每一条都有出处）：
///   · 铜排截面 = max(载流 I/J许用, 导热 Q·L/(k·ΔT))（<see cref="BusbarSizing.Result.SectionRequiredMm2"/> 同一条规则）；
///   · 铜排宽 = 舌端宽（铜排夹在舌端，锥形舌取舌端那一头）；厚 = 截面÷宽，向上取到 0.5 mm；
///   · 舌保温覆盖 = 圆盘切点到压接段前那一段（热模型里就是这一段包保温：<see cref="FlangePlate.InsulBoundaryXMm"/>
///     默认切点；压接段被铜排夹住，不包）；
///   · 焊脚 = max(板厚, 壁厚)，两面各一道（与 <see cref="DesignSpec.Plate"/> 的 WeldFilletLegMm 同源）。
/// </summary>
public static class FlangeKit
{
    public sealed class Row
    {
        public string Name = "";
        public double CurrentA;
        /// <summary>铜排截面 mm²：载流需要 / 导热需要 / 取大。</summary>
        public double SecCurMm2, SecHeatMm2, SecReqMm2;
        /// <summary>铜排规格 宽 × 厚 mm（宽 = 舌端宽；厚向上取到 0.5 mm）。</summary>
        public double BusWidthMm, BusThickMm;
        /// <summary>按规格反算的铜排电流密度 A/mm²。</summary>
        public double JCuAPerMm2;
        public double ClampLenMm, ClampTempC;
        /// <summary>夹持温度是设定值（参数表给定）还是算出来的（铜排热导那条边界）。</summary>
        public bool ClampTempIsInput;
        public double QClampW, BusGWPerK;
        /// <summary>舌保温 mm；覆盖从 x=From 到 x=To（法兰坐标，负向为舌片）。</summary>
        public double TabInsulMm, InsulFromXMm, InsulToXMm, InsulLenMm;
        public double WeldLegMm, PlateThickMm, TongueThickMm;
        public string ArmNote = "";
        public string Note = "";
    }

    /// <summary>厚度向上取到 0.5 mm，最少 1 mm（薄于 1 mm 的铜排现场不当母排用）。</summary>
    public static double RoundThickUp(double mm) => Math.Max(1.0, Math.Ceiling(mm * 2 - 1e-9) / 2.0);

    public static List<Row> Build(LineResult r, DesignSpec d, DesignInputs p)
    {
        var rows = new List<Row>();
        if (r is null || d is null || p is null) return rows;
        double width = 2 * d.TabHalfWidthMm;
        double xTan = d.TangentXMm();
        double xClamp = -d.TabLengthMm + d.ClampLengthMm;
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            var f = r.Flanges[j];
            var row = new Row { Name = f.Name, CurrentA = f.CurrentA };
            row.SecCurMm2 = f.BusSectionForCurrentMm2 > 1e-9
                ? f.BusSectionForCurrentMm2
                : f.CurrentA / Math.Max(1e-9, p.BusbarJAllowAPerMm2);
            row.SecHeatMm2 = Math.Max(0, f.BusSectionForHeatMm2);
            row.SecReqMm2 = Math.Max(row.SecCurMm2, row.SecHeatMm2);
            row.BusWidthMm = width;
            row.BusThickMm = width > 1e-9 ? RoundThickUp(row.SecReqMm2 / width) : double.NaN;
            row.JCuAPerMm2 = width > 1e-9 && row.BusThickMm > 0 ? f.CurrentA / (width * row.BusThickMm) : double.NaN;
            row.ClampLenMm = d.ClampLengthMm;
            row.ClampTempIsInput = p.BusbarClampTempC >= 0;
            row.ClampTempC = row.ClampTempIsInput ? p.BusbarClampTempC : f.TTabEndC;
            row.QClampW = f.QClampW;
            row.BusGWPerK = f.BusGWPerK;
            row.TabInsulMm = j < d.TabInsulMm.Length ? d.TabInsulMm[j] : double.NaN;
            row.InsulFromXMm = xTan; row.InsulToXMm = xClamp;
            row.InsulLenMm = Math.Max(0, xTan - xClamp);
            double t = j < d.TabThickMm.Length ? d.TabThickMm[j] : double.NaN;
            row.PlateThickMm = t;
            row.TongueThickMm = j < d.TongueThickMm.Length && !double.IsNaN(d.TongueThickMm[j]) ? d.TongueThickMm[j] : t;
            row.WeldLegMm = Math.Max(t, d.WallMm);
            row.ArmNote = d.HasTabArm(j) ? $"叉臂 {d.TabArmThickMm[j]:0.00} mm × [{d.TabArmX0Mm[j]:0}, {d.TabArmX1Mm[j]:0}]" : "";
            var notes = new List<string>();
            if (row.SecHeatMm2 > row.SecCurMm2 + 1e-9) notes.Add("导热要的截面比载流大，按导热选");
            if (!double.IsNaN(row.JCuAPerMm2) && row.JCuAPerMm2 > p.BusbarJAllowAPerMm2 + 1e-9) notes.Add("铜排电流密度超许用");
            if (row.InsulLenMm < 1) notes.Add("切点到压接段之间没有地方包保温");
            row.Note = string.Join("；", notes);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>输出框／安装报告用的文字版：一张制表位表（TextFmt 会排成 Excel 式）+ 几行通用配套。</summary>
    public static string Text(IReadOnlyList<Row> rows, DesignSpec d, DesignInputs p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("── 配套清单（铜排／保温／焊接）—— 按解出的法兰逐片给");
        sb.AppendLine("片\t电流 A\t铜排截面 mm²（载流／导热／取）\t铜排规格 宽×厚 mm\t铜排 J A/mm²\t压接 mm\t夹持 °C\t铜排带走 W\t舌保温 mm（覆盖 x 从…到）\t焊脚 mm\t备注");
        foreach (var r in rows)
            sb.AppendLine($"{r.Name}\t{r.CurrentA:0}\t{r.SecCurMm2:0}／{r.SecHeatMm2:0}／{r.SecReqMm2:0}" +
                          $"\t{r.BusWidthMm:0} × {r.BusThickMm:0.0}\t{r.JCuAPerMm2:0.00}\t{r.ClampLenMm:0}" +
                          $"\t{r.ClampTempC:0}{(r.ClampTempIsInput ? "" : "（算出）")}\t{r.QClampW:0.0}" +
                          $"\t{r.TabInsulMm:0.0}（{r.InsulFromXMm:0}…{r.InsulToXMm:0}，长 {r.InsulLenMm:0}）" +
                          $"\t{(double.IsNaN(r.WeldLegMm) ? "按图纸" : r.WeldLegMm.ToString("0.00"))}\t{(r.ArmNote.Length > 0 ? r.ArmNote + (r.Note.Length > 0 ? "；" : "") : "")}{r.Note}");   // R47 第三轮 N5：图纸档没有板厚，焊脚按图纸
        // ★ R48（2026-09-14，Opus 5）：圆盘保温按设计逐片取（DesignSpec.DiscInsulMmOf，与算例同一口径），不再读页面 DesignInputs。
        var discs = Enumerable.Range(0, d.FlangeCount).Select(d.DiscInsulMmOf).ToArray();
        string discInsul = discs.All(v => v <= 1e-6) ? "圆盘不包"
            : discs.All(v => Math.Abs(v - discs[0]) < 1e-9) ? $"圆盘双面包 {discs[0]:0.0} mm"
            : $"圆盘双面包，逐片 {string.Join(" / ", discs.Select(v => v.ToString("0.0")))} mm（入口 … 出口）";
        // ⚠ 2026-09-18 Opus 5 更正（同型病灶，与 InstallReport 第 5 节那一句同一处错）：
        //   这句原写「材料同参数表『② 中层』」，而这里引的 p.Layer1 在参数表上是**「① 内层（贴铂）」**
        //   （DesignInputs.Layer1 的 DisplayName；「② 中层」是 Layer2 = 致密氧化铝半管套，另一种材料）。
        //   名字与实物对不上 ⇒ 现场照这句去找材料会拿错。照实改。
        sb.AppendLine($"  保温：{discInsul}；管保温 {d.TubeInsulMm:0.0} mm；端部额外保温 {p.EndInsulExtraMm:0.0} mm × 长 {p.EndInsulLengthMm:0} mm；材料同参数表「① 内层（贴铂）」（{p.Layer1.Name}）。舌保温只包切点到压接段前那一段，压接段由铜排夹住不包。");
        sb.AppendLine($"  铜排：到冷端 {p.BusbarLenToSinkMm:0} mm，冷端 {p.BusbarSinkTempC:0} °C，许用电流密度 {p.BusbarJAllowAPerMm2:0.0} A/mm²；" +
                      (p.BusbarClampTempC >= 0 ? $"夹持温度是设定值 {p.BusbarClampTempC:0} °C（现场要把它整定到这个数）"
                                                 : "夹持温度由铜排热导算出（上表「算出」）") + "。");
        sb.AppendLine($"  焊接：角焊缝两面各一道，焊脚 = max(板厚, 壁厚)；手工 TIG，烧穿下界 {DesignInputs.WeldMinDefaultMm:0.0} mm；圆盘板厚工艺下界见「参考工具 ▸ 焊接下界小算盘」。");
        return sb.ToString();
    }
}
