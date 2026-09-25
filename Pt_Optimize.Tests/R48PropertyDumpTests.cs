using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// 物性函数的**不变性仪器**（2026-09-16 第五轮，Opus 5）。
///
/// 此前的「网格转储」只在 scratchpad（r48mat\dump → r48fix3\dump），且只含 MaterialDb 电阻率／持久强度与 Materials 四函数，
/// **不含膨胀函数** —— 第四轮把它当膨胀值不变的证据是拿错仪器（核验员 2026-09-16 指出）。核验员另写膨胀转储（verify4\xdump）
/// 证明第三轮→第四轮膨胀值 149707 行 0 个位差。两段都收进这里，格式逐字同那两个转储器（同一段的 SHA 可直接与 scratchpad 旧档比）：
///   · 网格段：12 牌号 × 0–1600 °C 步长 1 的 ResistivityOhmM／A／B／RuptureStressMPa（8 寿命）／AllowableMPa／RuptureLifeHours（7 应力）／
///     InCreepRange，四个非整数温度，Materials 四函数与常数，All.Keys／WithCreep／下拉顺序；
///   · 膨胀段：9 条曲线（系数、数据点、不可信段）× −10–1510 °C 每 0.5 K 六个曲线层函数与两类分档 + 12 牌号 × 3 借用开关 × 七个按牌号函数
///     + 12 牌号 × 990–1510 °C 每 0.5 K 的 RuptureStress（10 寿命）／RuptureLife（7 应力）。
/// 值写 double 的 16 进制位型（逐位），换行 Environment.NewLine（与转储器同）。文件写到测试 bin 下（不进交付目录），输出带时刻。
/// 两段 SHA-256 是**决定记录**：值有意改动时先改这里的记录再改代码 —— 记录就是「上次谁核过、核到哪」。慢测试。
/// </summary>
public class R48PropertyDumpTests
{
    private readonly ITestOutputHelper _o;
    public R48PropertyDumpTests(ITestOutputHelper o) => _o = o;

    private static string H(double v) => BitConverter.DoubleToInt64Bits(v).ToString("X16");
    private static string R(double t) => t.ToString("R", CultureInfo.InvariantCulture);
    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>
    /// 网格段决定记录。
    /// 2026-09-18 第七轮（Opus 5）由 <c>c49c724b…436d9e</c> 改为下面这个：<see cref="Materials.PtCp"/> 由
    /// <c>133.0 + 0.0135·T</c> 改为 Kaye &amp; Laby 四点最小二乘 ⇒ 本段里那 1601 行 <c>M</c> 必然变。
    /// **变的范围当场比过**（把 PtCp 改回旧式重编重跑，本段 SHA 就回到 <c>c49c724b…436d9e</c> 逐位；
    /// 新旧两份 20877 行逐行比，**只有 1601 行不同，全是 M 行，且每行只有最后一个字段（比热）变**
    /// —— 牌号段 G／逐温电阻率／持久强度／常数行 C 一位没动）〔实测：scratchpad grid_old.txt / grid_new.txt 逐行比〕。
    /// 膨胀段 SHA 不变（本轮没碰膨胀）。
    /// ⚠ 本段**没有覆盖**第七轮新加的 <see cref="PtGrade.ThermalKTable"/>／<see cref="PtGrade.CpTable"/> 的值
    ///   —— 那两条由 R48ThermalSourceTests 逐点对决定记录（比 SHA 更强），这里只是不重复。
    /// 改前的出处：第一轮改前基准 DLL 起未变（scratchpad r48fix3\dump\grid_r4b.txt、verify4\grid_v4.txt 同值）。
    /// </summary>
    public const string GridSha = "400073e31c1d9ee0e84924f2527fa0aa966911f53a2b90c8c62c2ada8958896d";
    /// <summary>
    /// 膨胀段决定记录。
    /// 2026-09-18（Opus 5，用户原话「改」）由 <c>af9d9079…3efffd</c> 改为下面这个：
    /// 工作簿 G18（PtRh20 @1400 °C）按 Platinum Metals Rev., 1960, 4 (4), p.139「20 % Rh」栏由 1.0157 改正为 1.0152，
    /// PtRh20 的 6 阶趋势线系数随之重算 ⇒ 这一段必然变。**变的范围当场比过**（改前源 vs 改后源，同一转储器，
    /// 149707 行）：21097 行不同，全部是 <c>CURVE PtRh20</c>（1 行）＋ <c>C PtRh20</c>（3048 行）＋
    /// <c>G Pt-Rh/80-20</c>（9024 行）＋ <c>G Umicore-PtRh20</c>（9024 行）—— 另外八条曲线与十个牌号**一位没动**
    /// 〔实测：scratchpad expansion_old.txt / expansion_new.txt 逐行比〕。网格段 SHA 不变（MaterialDb／Materials 没碰）。
    /// 改前的出处：核验员 verify4\dump_r4.txt（第四轮源）；第三轮源同值 0 个位差（dump_r3.txt 的 SHA 不同只因枚举改名）。
    /// </summary>
    public const string ExpansionSha = "e7fdce23c77e221c22c294affe8b6c7af04f92ac07fc7e44586009e8f9cc66b1";

    /// <summary>网格段：逐字同 scratchpad r48fix3\dump\Program.cs（2026-09-16）。</summary>
    public static string GridDump()
    {
        var sb = new StringBuilder();
        sb.AppendLine("keys " + string.Join("|", MaterialDb.All.Keys));
        sb.AppendLine("withcreep " + string.Join("|", MaterialDb.WithCreep.Select(g => g.Name)));
        sb.AppendLine("conv " + string.Join("|", new GradeNameConverter().GetStandardValues(null).Cast<object>()));
        double[] hrs = { 0.1, 1, 10, 100, 1000, 8760, 10000, 1e5 };
        double[] sig = { 0.5, 1, 2, 5, 10, 20, 50 };
        foreach (var g in MaterialDb.All.Values)
        {
            sb.AppendLine($"G {g.Name} {g.CreepSource} {H(g.Alpha)} {H(g.Beta)} {H(g.Rho0MicroOhmCm)} {H(g.T0C)} {g.HasResistivity} {H(g.DensityKgM3)} {H(g.MetalPriceRatio)} {H(g.FabricationPremium)} {H(g.CostPerKgRelative)} {g.HasCreep} {H(g.CreepTMinC)} {H(g.CreepTMaxC)} {g.RangeConfirmed} {string.Join(",", g.Ca.Select(H))} {string.Join(",", g.Cb.Select(H))}");
            for (int t = 0; t <= 1600; t++)
            {
                var l = new StringBuilder();
                l.Append(g.Name).Append(' ').Append(t).Append(' ').Append(H(g.ResistivityOhmM(t)));
                if (g.HasCreep)
                {
                    l.Append(' ').Append(H(g.A(t))).Append(' ').Append(H(g.B(t)));
                    foreach (var h in hrs) l.Append(' ').Append(H(g.RuptureStressMPa(t, h))).Append(' ').Append(H(g.AllowableMPa(t, h, 1.5)));
                    foreach (var s in sig) l.Append(' ').Append(H(g.RuptureLifeHours(t, s)));
                    l.Append(' ').Append(g.InCreepRange(t));
                }
                sb.AppendLine(l.ToString());
            }
            foreach (double t in new[] { 0.5, 1099.999, 1234.5678, 1500.25 })
                sb.AppendLine($"{g.Name} f{t} {H(g.ResistivityOhmM(t))}" + (g.HasCreep ? $" {H(g.RuptureStressMPa(t, 8760))} {H(g.RuptureLifeHours(t, 3.3))}" : ""));
        }
        for (int t = 0; t <= 1600; t++)
            sb.AppendLine($"M {t} {H(Materials.PtResistivity(t))} {H(Materials.PtTcr(t))} {H(Materials.PtThermalK(t))} {H(Materials.PtCp(t))}");
        sb.AppendLine($"C {H(Materials.RhoRef)} {H(Materials.AlphaFit)} {H(Materials.BetaFit)} {H(Materials.PtFitMaxC)} {H(Materials.PtAlphaExp)} {H(Materials.PtMeltC)} {H(Materials.PtDensity)}");
        return sb.ToString();
    }

    /// <summary>膨胀段：逐字同 scratchpad verify4\xdump\Program.cs（核验员，2026-09-16）。</summary>
    public static string ExpansionDump()
    {
        var sb = new StringBuilder();
        var temps = new System.Collections.Generic.List<double>();
        for (int i = -20; i <= 3020; i++) temps.Add(0.5 * i);
        temps.AddRange(new[] { double.NaN, 99.999, 1000.25, 1234.5678, 1321.99, 1322.01, 315.9, 315.95 });
        sb.AppendLine("curves " + string.Join("|", PtThermalExpansion.Curves.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        foreach (var c in PtThermalExpansion.Curves.Values.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            sb.AppendLine($"CURVE {c.Id} order {c.Order} coeffs {string.Join(",", c.Coeffs.Select(H))} data {string.Join(",", c.DataTempsC.Select(H))} alpha {string.Join(",", c.DataAlphaE6.Select(H))} untrusted {string.Join(";", c.UntrustedSegments.Select(s => H(s.LoC) + "-" + H(s.HiC)))}");
            foreach (double t in temps)
                sb.AppendLine($"C {c.Id} {R(t)} {H(c.RawMeanAlphaE6(t))} {H(c.RawInstantAlphaE6(t))} {H(c.RawStrain(t))} {H(c.MeanAlphaE6At(t))} {H(c.StrainAt(t))} {H(c.InstantAlphaE6At(t))} {c.Classify(t, false, out _)} {c.Classify(t, true, out _)}");
        }
        foreach (string g in MaterialDb.All.Keys.OrderBy(k => k, StringComparer.Ordinal))
            foreach (var b in Enum.GetValues<ExpansionBorrowOption>())
                foreach (double t in temps)
                {
                    var ma = PtThermalExpansion.MeanAlphaE6(g, t, b); var lr = PtThermalExpansion.LengthRatio(g, t, b); var st = PtThermalExpansion.Strain(g, t, b);
                    var ia = PtThermalExpansion.InstantAlphaE6(g, t, b); var hl = PtThermalExpansion.HotLength(g, 780, t, b); var el = PtThermalExpansion.Elongation(g, 780, t, b);
                    var sd = PtThermalExpansion.StrainDifference(g, t, "Pt", t - 10, b);
                    static string V(ExpansionValue v) => $"{H(v.Value)}/{v.Coverage}/{v.Link}/{v.Curve}";
                    sb.AppendLine($"G {g} {b} {R(t)} {V(ma)} {V(lr)} {V(st)} {V(ia)} {V(hl)} {V(el)} {V(sd)}");
                }
        double[] hrs = { 0.01, 0.1, 1, 10, 100, 1000, 8760, 10000, 1e5, 1e6 };
        double[] sig = { 0.5, 1, 2, 5, 10, 20, 50 };
        foreach (var g in MaterialDb.All.Keys.OrderBy(k => k, StringComparer.Ordinal))
            for (int i = 1980; i <= 3020; i++)
            {
                double t = 0.5 * i;
                var l = new StringBuilder();
                l.Append("K ").Append(g).Append(' ').Append(R(t));
                foreach (double h in hrs) { var v = PtCreepWorkbook.RuptureStress(g, t, h); l.Append(' ').Append(H(v.Value)).Append('/').Append(v.Coverage); }
                foreach (double s in sig) { var v = PtCreepWorkbook.RuptureLife(g, t, s); l.Append(' ').Append(H(v.Value)).Append('/').Append(v.Coverage); }
                sb.AppendLine(l.ToString());
            }
        return sb.ToString();
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 物性转储两段_SHA等于决定记录_文件写到bin并带时刻()
    {
        var t0 = DateTime.Now;
        string grid = GridDump();
        string exp = ExpansionDump();
        string dir = Path.Combine(AppContext.BaseDirectory, "物性转储");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "grid.txt"), grid);
        File.WriteAllText(Path.Combine(dir, "expansion.txt"), exp);
        string gs = Sha(grid), es = Sha(exp);
        _o.WriteLine($"{t0:yyyy-MM-dd HH:mm:ss} 起、{DateTime.Now:HH:mm:ss} 止；写到 {dir}");
        _o.WriteLine($"网格段 {grid.Count(ch => ch == '\n')} 行 SHA-256 {gs}（决定记录 {GridSha}）");
        _o.WriteLine($"膨胀段 {exp.Count(ch => ch == '\n')} 行 SHA-256 {es}（决定记录 {ExpansionSha}）");
        Assert.True(grid.Count(ch => ch == '\n') > 20000 && exp.Count(ch => ch == '\n') > 140000, "反自证：两段行数不对，转储器坏了");
        Assert.True(gs == GridSha, $"网格段 SHA {gs} ≠ 决定记录 {GridSha}：MaterialDb／Materials 的值变了 —— 有意就先改记录、再改代码");
        Assert.True(es == ExpansionSha, $"膨胀段 SHA {es} ≠ 决定记录 {ExpansionSha}：膨胀／持久强度函数的值或覆盖类别变了 —— 有意就先改记录、再改代码");
    }
}
