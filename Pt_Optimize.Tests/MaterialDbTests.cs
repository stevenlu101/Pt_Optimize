using System;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// 材料数据库验证 —— 对照用户工作簿里的**原始实测点**，
/// 确认多项式模型（及我的系数转录）能复现供应商数据。
/// 数据源：鉑金材料蠕變應力壽命估算.xlsx (R32–R112)、鉑金電氣計算.xlsx (R23–R44)
/// </summary>
public class MaterialDbTests
{
    private readonly ITestOutputHelper _o;
    public MaterialDbTests(ITestOutputHelper o) => _o = o;

    // (牌号, 温度°C, 时间[h]…, 应力[MPa]…)
    private static readonly (string grade, double tC, double[] t, double[] s)[] Raw =
    {
        ("Tanaka-ZGS-Pt", 1500, new[]{1d,10,100,1000,10000}, new[]{24d,13,7.1,4,2.3}),
        ("Tanaka-ZGS-Pt", 1400, new[]{1d,10,100,1000,10000}, new[]{25.5,16,10,6.5,4.3}),
        ("Tanaka-ZGS-Pt", 1300, new[]{1d,10,100,1000,10000}, new[]{34d,22.6,15.5,10.5,6.9}),
        ("Tanaka-ZGS-Pt", 1200, new[]{1d,10,100,1000,10000}, new[]{40d,26,18,12,8.4}),
        ("Tanaka-ZGS-Pt", 1100, new[]{1d,10,100,1000,10000}, new[]{44.5,32.5,24,17.5,13.4}),
        ("Tanaka-ZGS-Pt", 1000, new[]{1d,10,100,1000,10000}, new[]{48d,36.8,28.3,21.7,16.6}),

        ("Tanaka-ZGS-PtRh10", 1400, new[]{1d,10,100,1000,10000}, new[]{57d,31.7,18.5,10.9,6.2}),
        ("Tanaka-ZGS-PtRh10", 1300, new[]{1d,10,100,1000,10000}, new[]{71d,43,26.5,16.3,10}),
        ("Tanaka-ZGS-PtRh10", 1200, new[]{1d,10,100,1000,10000}, new[]{74d,48,31.5,21.3,14}),
        ("Tanaka-ZGS-PtRh10", 1100, new[]{1d,10,100,1000,10000}, new[]{81.2,58.3,41.5,29.3,21.3}),

        ("Pt-Rh/90-10", 1400, new[]{1d,10,100,1000,10000}, new[]{18d,9.3,4.8,2.4,1.25}),
        ("Pt-Rh/90-10", 1300, new[]{1d,10,100,1000,10000}, new[]{24.6,12.6,6.6,3.4,1.75}),
        ("Pt-Rh/90-10", 1200, new[]{1d,10,100,1000,10000}, new[]{33.1,17.4,9.1,4.7,2.43}),
        ("Pt-Rh/90-10", 1100, new[]{1d,10,100,1000,10000}, new[]{55d,27.5,13.5,6.8,3.4}),

        ("Pt-Rh/80-20", 1400, new[]{1d,10,100,1000,10000}, new[]{28.9,13.3,5.8,2.65,1.2}),
        ("Pt-Rh/80-20", 1300, new[]{1d,10,100,1000,10000}, new[]{38.6,18.7,9,4.35,2.12}),
        ("Pt-Rh/80-20", 1200, new[]{1d,10,100,1000,10000}, new[]{53d,26.2,12.8,6.3,3}),
        ("Pt-Rh/80-20", 1100, new[]{1d,10,100,1000,10000}, new[]{85.5,42,20.4,9.9,4.8}),

        // 纯铂的时间轴是 0.1–1000 h（该温度下寿命本就短一个量级）
        ("Pt", 1400, new[]{0.1,1,10,100,1000}, new[]{6.7,4.2,2.65,1.7,1.05}),
        ("Pt", 1300, new[]{0.1,1,10,100,1000}, new[]{14.5,8.4,5,2.9,1.7}),
        ("Pt", 1200, new[]{0.1,1,10,100,1000}, new[]{25d,13.3,7,3.7,2}),
        ("Pt", 1100, new[]{0.1,1,10,100,1000}, new[]{26.3,15.7,9.6,5.7,3.4}),
    };

    [Fact]
    public void Creep_ReproducesSupplierRawData()
    {
        double worst = 0; string worstAt = "";
        int n = 0; double sumRel = 0;
        foreach (var (grade, tC, ts, ss) in Raw)
        {
            var g = MaterialDb.Get(grade);
            for (int i = 0; i < ts.Length; i++)
            {
                double model = g.RuptureStressMPa(tC, ts[i]);
                double rel = Math.Abs(model - ss[i]) / ss[i];
                sumRel += rel; n++;
                if (rel > worst) { worst = rel; worstAt = $"{grade} {tC}°C {ts[i]}h 实测{ss[i]} 模型{model:F2}"; }
            }
        }
        _o.WriteLine($"对照 {n} 个实测点：平均相对偏差 {sumRel / n * 100:F2} %，最大 {worst * 100:F1} %");
        _o.WriteLine($"最差点：{worstAt}");
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
        // 电气工作簿 R5: Pt @1150 °C → 47.0803 μΩ·cm
        double rho = MaterialDb.Get("Pt").ResistivityOhmM(1150) * 1e8;
        _o.WriteLine($"Pt @1150 °C: 模型 {rho:F4} μΩ·cm，工作簿 47.0803");
        Assert.InRange(Math.Abs(rho - 47.080255209905694), 0, 1e-6);

        // 与既有 Materials.PtResistivity 必须一致（同一套拟合）
        for (double t = 0; t <= 1500; t += 100)
            Assert.InRange(Math.Abs(MaterialDb.Get("Pt").ResistivityOhmM(t)
                                    - Materials.PtResistivity(t)), 0, 1e-15);
    }
}
