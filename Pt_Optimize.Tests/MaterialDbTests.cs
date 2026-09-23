using System;
using System.Collections.Generic;
using System.Linq;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// 材料数据库验证 —— 对照用户工作簿里的**原始实测点**，
/// 确认多项式模型（及我的系数转录）能复现供应商数据。
/// 数据源：鉑金材料蠕變應力壽命估算.xlsx (R32–R112)、鉑金電氣計算.xlsx (R5、R23–R44)
///
/// ★ 2026-09-15，Opus 5：原始点**改为每次直接读 xlsx**（CreepXlsxRaw，只读 G:L 列）。
///   此前这里手抄了 110 个点，漏了纯铂 10000 h 一列（L103/L106/L109/L112）与 ZGS-PtRh10 的 1500、1000 °C 两行，
///   并把纯铂时间轴注成「0.1–1000 h」（工作簿 R102 是 0.1–10000 h）—— 手抄的门守不住手抄的病。
///   补回后 124 点；20 % 门槛**不动**（不因看到最大 4.6 % 事后收紧，也不放宽）。
///   牌号映射取生产代码 PtCreepWorkbook.CoefRows（它自己由 R48CreepWorkbookTests 对着系数表核）。
/// </summary>
public class MaterialDbTests
{
    private readonly ITestOutputHelper _o;
    public MaterialDbTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void Creep_ReproducesSupplierRawData()
    {
        using var book = XlsxBook.OpenInRepo(PtCreepWorkbook.WorkbookFile);
        var blocks = CreepXlsxRaw.Read(book.Sheet(PtCreepWorkbook.SheetName));
        Assert.NotEmpty(blocks);

        double worst = 0; string worstAt = "";
        int n = 0; double sumRel = 0;
        var byGrade = new Dictionary<string, List<(double tC, double h, double meas, double model, double rel, double dLogS, double dLogT)>>();
        foreach (var b in blocks)
        {
            string grade = PtCreepWorkbook.CoefRows.Single(c => c.RawBlockTitle == b.Title).Grade;
            var g = MaterialDb.Get(grade);
            if (!byGrade.TryGetValue(grade, out var list)) byGrade[grade] = list = new();
            for (int i = 0; i < b.Hours.Length; i++)
            {
                double model = g.RuptureStressMPa(b.TempC, b.Hours[i]);
                double rel = Math.Abs(model - b.Stress[i]) / b.Stress[i];
                sumRel += rel; n++;
                list.Add((b.TempC, b.Hours[i], b.Stress[i], model, (model - b.Stress[i]) / b.Stress[i],
                          Math.Log10(model) - Math.Log10(b.Stress[i]),
                          Math.Log10(g.RuptureLifeHours(b.TempC, b.Stress[i])) - Math.Log10(b.Hours[i])));
                if (rel > worst) { worst = rel; worstAt = $"{grade} {b.TempC}°C {b.Hours[i]}h 实测{b.Stress[i]} 模型{model:F2}"; }
            }
        }
        _o.WriteLine($"对照 {n} 个实测点：平均相对偏差 {sumRel / n * 100:F2} %，最大 {worst * 100:F1} %");
        _o.WriteLine($"最差点：{worstAt}");
        foreach (var (grade, list) in byGrade)
        {
            var w = list.OrderByDescending(x => Math.Abs(x.rel)).First();
            var unsafeMax = list.OrderByDescending(x => x.rel).First();
            _o.WriteLine($"  {grade}：{list.Count} 点，平均 {list.Average(x => Math.Abs(x.rel)) * 100:F2} %，"
                + $"最大 {Math.Abs(w.rel) * 100:F2} % @ {w.tC:0} °C/{w.h:g} h（模型{(w.rel > 0 ? "偏高·偏不安全" : "偏低·保守")}）；"
                + $"log σ 残差最大 {list.Max(x => Math.Abs(x.dLogS)):F4}，寿命当量 log t 最大 {list.Max(x => Math.Abs(x.dLogT)):F3}"
                + $"（×{Math.Pow(10, list.Max(x => Math.Abs(x.dLogT))):F2}）；"
                + $"模型偏高最多的点 {unsafeMax.tC:0} °C/{unsafeMax.h:g} h 实测 {unsafeMax.meas} 模型 {unsafeMax.model:F3}（{unsafeMax.rel * 100:+0.00;-0.00} %）");
        }
        Assert.True(worst < 0.20, $"最大偏差 {worst * 100:F1} % 超过 20 % —— 系数转录可能有误：{worstAt}");
    }

    [Fact]
    public void Creep_LifeAndStressAreInverse()
    {
        // 由应力求寿命、再由寿命求应力，必须回到原值
        foreach (var g in MaterialDb.WithCreep)
            foreach (double tC in new[] { 1100.0, 1200, 1300 })
                foreach (double s in new[] { 2.0, 5.0, 10.0 })
                {
                    double life = g.RuptureLifeHours(tC, s);
                    if (double.IsNaN(life) || life <= 0 || double.IsInfinity(life)) continue;
                    double back = g.RuptureStressMPa(tC, life);
                    Assert.InRange(Math.Abs(back - s) / s, 0, 1e-9);
                }
    }

    [Fact]
    public void Resistivity_MatchesWorkbookFit()
    {
        // 电气工作簿 R5：C5 牌号、H5 温度、I5 电阻率缓存 —— 2026-09-15，Opus 5 改为直接读 xlsx（原来手抄 47.080255209905694）
        using var book = XlsxBook.OpenInRepo(PtResistivityData.WorkbookFile);
        var sh = book.Sheet(PtResistivityData.SheetName);
        Assert.Equal("Pt", sh.Str("C5").Trim());
        double tC = sh.Num("H5"), cache = sh.Num("I5");
        double rho = MaterialDb.Get("Pt").ResistivityOhmM(tC) * 1e8;
        _o.WriteLine($"Pt @{tC} °C: 模型 {rho:F4} μΩ·cm，工作簿 I5 {cache:F4}");
        Assert.InRange(Math.Abs(rho - cache), 0, 1e-6);

        // 与既有 Materials.PtResistivity 必须一致（同一套拟合）。2026-09-15，Opus 5：由 1e-15 绝对容差收紧为逐位
        for (double t = 0; t <= 1500; t += 100)
            Assert.Equal(BitConverter.DoubleToInt64Bits(MaterialDb.Get("Pt").ResistivityOhmM(t)),
                         BitConverter.DoubleToInt64Bits(Materials.PtResistivity(t)));
    }

    /// <summary>
    /// 数据齐全度（2026-09-16 立，2026-09-18 加第四类「热导率与比热」并改借用口径，Opus 5）。
    ///
    /// 用户 2026-09-16 原话：「数据不全(电阻/热膨胀/蠕变应力)的铂金合金先以灰色不可选展示，只用数据全的铂金合金」；
    /// 用户 2026-09-17/18：可选牌号 = Pt、Pt-Rh/90-10、Pt-Rh/80-20、Tanaka-ZGS-Pt、Tanaka-ZGS-PtRh10，
    /// 「铂铑 10/20 的热导率和比热：上网找带出处的数，找不到就把两个铂铑牌号也灰显直到有数」。
    /// 查证结果（旁证 B，2026-09-18）：Pt-10%Rh 有 Heraeus 的 k、cp 温度函数；**Pt-20%Rh 一个实测点都没有**
    /// ⇒ 齐全的恰是 Pt、Pt-Rh/90-10、Tanaka-ZGS-Pt、Tanaka-ZGS-PtRh10 四个（Pt-Rh/80-20 缺热导率与比热）。
    ///
    /// 决定记录逐牌号写死（不是抄生产配方）。借用口径 2026-09-18 由「一律不算自己的」放宽为
    /// **「同名义成分才算」**（待定 9 结案，见 MaterialDb.DataCompleteness 注记）——
    /// 所以 Tanaka-ZGS 两个牌号的电阻率借用不再算缺项，但**必须仍在 Borrowed 里逐条点名**（本门核这一条）。
    ///
    /// 注入「把借用当自己的（不看名义成分）」「膨胀 Borrowed 一律算 Direct」「持久强度不看 RangeConfirmed」
    /// 「第四类根本不查」这里必须红。
    /// </summary>
    [Fact]
    public void 数据齐全度_四类齐全的只有四个牌号_借用与推定逐项点名()
    {
        // (牌号, 电阻率自己的/同成分, 膨胀自己的/同成分, 持久强度有原始点, 热导率与比热有, 缺项里必须出现的字, 借用里必须出现的字)
        var decided = new (string Grade, bool R, bool E, bool C, bool T, string[] MissingMustName, string[] BorrowMustName)[]
        {
            ("Pt", true, true, true, true, Array.Empty<string>(), Array.Empty<string>()),
            ("Pt-Rh/90-10", true, true, true, true, Array.Empty<string>(), Array.Empty<string>()),
            ("Pt-Rh/80-20", true, true, true, false, new[] { "热导率与比热：无数据" }, Array.Empty<string>()),
            ("Tanaka-ZGS-Pt", true, true, true, true, Array.Empty<string>(),
                new[] { "电阻率：借用 Pt 行（同名义成分 Pt）", "热导率与比热：借用 Pt 的数据（同名义成分 Pt）" }),
            ("Tanaka-ZGS-PtRh10", true, true, true, true, Array.Empty<string>(),
                new[] { "电阻率：借用 Pt-Rh/90-10 行（同名义成分 Pt-10Rh）",
                        "热导率与比热：借用 Pt-Rh/90-10 的数据（同名义成分 Pt-10Rh）" }),
            ("Umicore-PtRh10", true, true, false, true, new[] { "持久强度：拟合区间 1000–1500 °C 为推定" },
                new[] { "电阻率：借用 Pt-Rh/90-10 行", "热膨胀：借用 PtRh10 曲线", "热导率与比热：借用 Pt-Rh/90-10 的数据" }),
            ("Umicore-PtRh20", true, true, false, false,
                new[] { "持久强度：拟合区间 1000–1500 °C 为推定", "热导率与比热：无数据" },
                new[] { "电阻率：借用 Pt-Rh/80-20 行", "热膨胀：借用 PtRh20 曲线" }),
            ("FKS16/Pt", true, false, false, false,
                new[] { "热膨胀：工作簿没有本牌号", "持久强度：拟合区间 1000–1500 °C 为推定", "热导率与比热：无数据" },
                Array.Empty<string>()),
            ("FKS16/PtRh-9010", true, false, false, false,
                new[] { "热膨胀：工作簿没有本牌号", "持久强度：拟合区间 1000–1500 °C 为推定", "热导率与比热：无数据" },
                Array.Empty<string>()),
            ("Pd", true, false, false, false,
                new[] { "热膨胀：工作簿没有本牌号", "持久强度：无数据", "热导率与比热：无数据" }, Array.Empty<string>()),
            ("Ni", true, false, false, false,
                new[] { "热膨胀：工作簿没有本牌号", "持久强度：无数据", "热导率与比热：无数据" }, Array.Empty<string>()),
            ("Cu", true, false, false, false,
                new[] { "热膨胀：工作簿没有本牌号", "持久强度：无数据", "热导率与比热：无数据" }, Array.Empty<string>()),
        };
        Assert.True(decided.Select(d => d.Grade).ToHashSet().SetEquals(MaterialDb.All.Keys),
            "决定记录与材料库牌号不一一对应 —— 新加牌号先在这里定齐不齐全：缺 " + string.Join("、", MaterialDb.All.Keys.Except(decided.Select(d => d.Grade))));
        var complete = new List<string>();
        foreach (var d in decided)
        {
            var c = MaterialDb.DataCompleteness(d.Grade);
            Assert.True(c.Grade == d.Grade && c.OwnResistivity == d.R && c.OwnExpansion == d.E
                        && c.OwnCreepWithPoints == d.C && c.OwnThermal == d.T,
                $"{d.Grade}：应为 电阻率{(d.R ? "有" : "无")}／膨胀{(d.E ? "有" : "无")}／持久强度{(d.C ? "有原始点" : "无原始点")}／热导率与比热{(d.T ? "有" : "无")}，"
                + $"判定却是 {c.OwnResistivity}／{c.OwnExpansion}／{c.OwnCreepWithPoints}／{c.OwnThermal}");
            Assert.True(c.IsComplete == (d.R && d.E && d.C && d.T), $"{d.Grade}：IsComplete 不等于四者皆真");
            int expectMissing = (d.R ? 0 : 1) + (d.E ? 0 : 1) + (d.C ? 0 : 1) + (d.T ? 0 : 1);
            Assert.True(c.Missing.Length == expectMissing, $"{d.Grade}：缺项应 {expectMissing} 条，实际 {c.Missing.Length}：{string.Join("；", c.Missing)}");
            foreach (string must in d.MissingMustName)
                Assert.True(c.Missing.Any(x => x.Contains(must, StringComparison.Ordinal)), $"{d.Grade}：缺项里没点名「{must}」：{string.Join("；", c.Missing)}");
            foreach (string must in d.BorrowMustName)
                Assert.True(c.Borrowed.Any(x => x.Contains(must, StringComparison.Ordinal)),
                    $"{d.Grade}：借用清单里没点名「{must}」—— 同名义成分借用可以算齐全，但**不许不说**：{string.Join("；", c.Borrowed)}");
            Assert.True(c.Borrowed.Length == d.BorrowMustName.Length,
                $"{d.Grade}：借用清单应 {d.BorrowMustName.Length} 条，实际 {c.Borrowed.Length}：{string.Join("；", c.Borrowed)}");
            Assert.True(c.Missing.All(x => x.Contains('：')) && c.Missing.Distinct().Count() == c.Missing.Length, $"{d.Grade}：缺项要成句、不重复");
            Assert.True(c.Borrowed.All(x => x.Contains('：')) && c.Borrowed.Distinct().Count() == c.Borrowed.Length, $"{d.Grade}：借用项要成句、不重复");
            if (c.IsComplete) complete.Add(d.Grade);
            _o.WriteLine($"{d.Grade}：{(c.IsComplete ? "齐全" : "不齐全 —— " + string.Join("；", c.Missing))}"
                       + (c.Borrowed.Length > 0 ? "　〔同成分借用：" + string.Join("；", c.Borrowed) + "〕" : ""));
        }
        Assert.True(complete.ToHashSet().SetEquals(new[] { "Pt", "Pt-Rh/90-10", "Tanaka-ZGS-Pt", "Tanaka-ZGS-PtRh10" }),
            "齐全的牌号应恰为 Pt、Pt-Rh/90-10、Tanaka-ZGS-Pt、Tanaka-ZGS-PtRh10（决定记录；Pt-Rh/80-20 缺热导率与比热），实际："
            + string.Join("、", complete));
        Assert.True(MaterialDb.DataCompleteness(new DesignInputs().GradeName).IsComplete, "默认设计牌号（纯铂）必须齐全");
        Assert.Throws<KeyNotFoundException>(() => MaterialDb.DataCompleteness("PtRh10"));
    }

    /// <summary>
    /// ★ **合取式的每一种组合都要有人走过**（2026-09-18，Opus 5）。
    ///
    /// 病灶：上一轮的齐全度门只走库里现有的 12 个牌号。那 12 组 (电阻率, 膨胀, 持久强度) 真值里
    /// **没有 (真,真,假) 之外的花样**，于是「合取式少写一项」这种改法门照样绿 —— 门空转。
    /// 本轮加第四项后同样的洞会再开一次：真正要钉的是 (真,真,真,假) —— 唯独第四项为假。
    /// 做法：<see cref="MaterialDb.Compose"/> 是纯函数，这里用**人造牌号**喂 16 种组合，一种都不漏。
    /// 注入「IsComplete 少乘一项」「用 || 代替 &amp;&amp;」这里必红。
    /// </summary>
    [Fact]
    public void 齐全度的合取式_十六种真假组合逐一钉死_人造牌号()
    {
        int n = 0;
        for (int mask = 0; mask < 16; mask++)
        {
            bool r = (mask & 1) != 0, e = (mask & 2) != 0, c = (mask & 4) != 0, t = (mask & 8) != 0;
            var miss = new List<string>();
            if (!r) miss.Add("电阻率：人造牌号，无数据");
            if (!e) miss.Add("热膨胀：人造牌号，无数据");
            if (!c) miss.Add("持久强度：人造牌号，无数据");
            if (!t) miss.Add("热导率与比热：人造牌号，无数据");
            var got = MaterialDb.Compose($"人造牌号-{mask}", r, e, c, t, miss, Array.Empty<string>());
            Assert.True(got.IsComplete == (r && e && c && t),
                $"人造牌号-{mask}（{r}/{e}/{c}/{t}）：IsComplete 应为 {(r && e && c && t)}，实际 {got.IsComplete}");
            Assert.Equal(miss.Count, got.Missing.Length);
            n++;
        }
        Assert.Equal(16, n);
        // 单独把「只有第四项为假」钉一次：这正是「第四类没查」时会溜过去的那一格
        var only4 = MaterialDb.Compose("人造牌号-只缺热导率", true, true, true, false,
                                       new[] { "热导率与比热：人造牌号，无数据" }, Array.Empty<string>());
        Assert.False(only4.IsComplete, "三项全真、只缺热导率与比热 ⇒ 必须不齐全（这一格漏了，Pt-Rh/80-20 就会被当成齐全）");
        _o.WriteLine("16 种真假组合全部走过；(真,真,真,假) 单独钉死");
    }
}
