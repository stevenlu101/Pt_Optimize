using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 铂系材料数据库 —— 电阻率、持久强度来自用户提供的实测工作簿；热导率、比热来自文献与厂方图（逐条出处见 SetThermal）。
///
///   电阻率：鉑金電氣計算.xlsx    ρ(T) = ρ₀·[1 + α(T−T₀) + β(T−T₀)²]   [μΩ·cm]
///   持久强度：鉑金材料蠕變應力壽命估算.xlsx
///             T·log₁₀σ = a(T)·log₁₀t + b(T)，a、b 为 T(K) 的五次多项式
///
/// 该蠕变式比单一 Larson–Miller 直线更灵活：斜率 a 也随温度变，
/// 因而能同时贴合弥散强化材料（曲线平缓）与纯铂（曲线陡）。
/// </summary>
/// <summary>
/// 一条按温度的物性曲线（热导率／比热）。2026-09-18，Opus 5。
///
/// 两种形态，**值只有一个来源**：
///   · 表式（<see cref="FromTable"/> = true）：值由 <see cref="TC"/>／<see cref="V"/> 逐点**线性内插**；
///     区间外**保持端点值**（不外插）—— Pt-10%Rh 的 k 在 1200 °C 以上已经走平，外插会给出没有依据的上升。
///     区间外与否由 <see cref="Covers"/> 报出，调用方必须把「外推」带进报告。
///   · 拟合式（<see cref="FromTable"/> = false）：值由 <see cref="Fit"/> 给（求解链用的就是它），
///     <see cref="TC"/>／<see cref="V"/> 只是**拟合／核对所用的原始点**，是出处，不是第二份数值来源。
/// </summary>
public sealed class PtPropCurve
{
    /// <summary>量名，全名成句，不出代号：「热导率」「比热」</summary>
    public string Quantity = "";
    public string Unit = "";
    /// <summary>数据点温度 °C（升序）</summary>
    public double[] TC = Array.Empty<double>();
    /// <summary>数据点值</summary>
    public double[] V = Array.Empty<double>();
    public bool FromTable = true;
    /// <summary>拟合式形态下的取值函数（表式为 null）</summary>
    public Func<double, double>? Fit;
    /// <summary>来源：出版方／年份／URL／图号，逐字可查</summary>
    public string Source = "";
    /// <summary>实测还是内插还是外推、分辨率、带宽 —— 随值带出</summary>
    public string Note = "";

    public double TMinC => TC.Length > 0 ? TC[0] : double.NaN;
    public double TMaxC => TC.Length > 0 ? TC[^1] : double.NaN;

    /// <summary>该温度在数据点覆盖区间内（false ⇒ 外推／保端点值）</summary>
    public bool Covers(double tC) => TC.Length > 0 && tC >= TC[0] - 1e-9 && tC <= TC[^1] + 1e-9;

    public double Value(double tC)
    {
        if (!FromTable) return Fit is null ? double.NaN : Fit(tC);
        if (TC.Length == 0) return double.NaN;
        if (tC <= TC[0]) return V[0];
        if (tC >= TC[^1]) return V[^1];
        int i = 1;
        while (i < TC.Length - 1 && tC > TC[i]) i++;
        double f = (tC - TC[i - 1]) / (TC[i] - TC[i - 1]);
        return V[i - 1] + f * (V[i] - V[i - 1]);
    }

    /// <summary>值 + 出处 + 覆盖情况，一句话（界面／报告直接用）</summary>
    public string Describe(double tC)
        => $"{Quantity} {Value(tC):0.###} {Unit} @ {tC:0} °C" + (Covers(tC) ? "" : $"（{TMinC:0}–{TMaxC:0} °C 之外，取端点值，属外推）")
         + $"；{Note}；出处：{Source}";
}

public sealed class PtGrade
{
    public string Name = "";
    public string CreepSource = "";          // 蠕变数据对应的牌号名（可能与电阻率牌号不同）

    // 电阻率
    public double Alpha, Beta, Rho0MicroOhmCm, T0C;
    public bool HasResistivity;

    /// <summary>
    /// 电阻率数据取自《鉑金電氣計算.xlsx》哪个牌号那一行。= <see cref="Name"/> ⇒ 本牌号自己的行；
    /// 否则是**借用**（<see cref="MaterialDb"/> 里 AddCreepOnly 的牌号：ZGS / Umicore 借同族电阻率）。
    /// 2026-09-15，Opus 5：此前数据模型里没有「借用」标记，借用是静默的；本字段只加标记，不改任何数值。
    /// 覆盖查询见 <see cref="PtResistivityData.Read"/>。
    /// </summary>
    public string ResistivityFrom = "";

    /// <summary>
    /// **名义成分**（判「借用算不算自己的数据」用）。2026-09-18，Opus 5。
    /// 同一个名义成分下的不同牌号（纯铂 ↔ ZGS-Pt ↔ FKS16/Pt；PtRh10 ↔ ZGS-PtRh10 ↔ Umicore-PtRh10）
    /// 差的是弥散相（体积分数 &lt;1 %）与供应商，不是成分。逐牌号显式登记见 <see cref="MaterialDb.SetCompositions"/>。
    /// </summary>
    public string Composition = "";

    /// <summary>
    /// 热导率 k(T)。null = 本牌号没有热导率数据（<see cref="MaterialDb.DataCompleteness"/> 第四类据此判缺）。
    /// 2026-09-18，Opus 5。
    /// </summary>
    public PtPropCurve? ThermalKTable;

    /// <summary>比热 cp(T)。null = 本牌号没有比热数据。2026-09-18，Opus 5。</summary>
    public PtPropCurve? CpTable;

    /// <summary>
    /// 热导率／比热取自哪个牌号。= <see cref="Name"/> ⇒ 本牌号自己的数据；否则是**借用**
    /// （与 <see cref="ResistivityFrom"/> 同口径，报告里必须点名）。2026-09-18，Opus 5。
    /// </summary>
    public string ThermalFrom = "";

    /// <summary>
    /// **推算的参考值，不入任何判定**（<see cref="MaterialDb.DataCompleteness"/> 不看它，求解链不读它）。
    /// 目前只有 Pt-20%Rh 的 Wiedemann–Franz 推算热导率。2026-09-18，Opus 5。
    /// </summary>
    public PtPropCurve? ThermalKAdvisory;

    /// <summary>电阻率拟合本身的存疑之处（空 = 无）。随值带出，不裁决。2026-09-18，Opus 5。</summary>
    public string ResistivityCaveat = "";
    /// <summary>上条存疑对应的建议带宽（相对值；0 = 不带）。</summary>
    public double ResistivityBandFrac;

    /// <summary>热导率与比热两条都在（借来的也算在，借没借看 <see cref="ThermalFrom"/>）</summary>
    public bool HasThermal => ThermalKTable is not null && CpTable is not null;

    /// <summary>热导率 W/(m·K)；没有数据返回 NaN。</summary>
    public double ThermalKWPerMK(double tC) => ThermalKTable?.Value(tC) ?? double.NaN;
    /// <summary>比热 J/(kg·K)；没有数据返回 NaN。</summary>
    public double CpJPerKgK(double tC) => CpTable?.Value(tC) ?? double.NaN;

    /// <summary>密度 kg/m³。合金密度低于纯铂，同体积质量更小</summary>
    public double DensityKgM3 = 21450;

    /// <summary>金属价值相对纯铂的倍数（按 Rh 含量与现货价推算，不含加工溢价）</summary>
    public double MetalPriceRatio = 1.0;

    /// <summary>加工溢价倍数（弥散强化等特殊工艺）。默认 1.0，须以供应商报价替换</summary>
    public double FabricationPremium = 1.0;

    public double CostPerKgRelative => MetalPriceRatio * FabricationPremium;

    // 蠕变：a(T)、b(T) 的五次多项式系数 c5..c0
    public double[] Ca = Array.Empty<double>();
    public double[] Cb = Array.Empty<double>();
    public bool HasCreep;

    /// <summary>
    /// 蠕变拟合的**有效温度区间** [°C]。多项式在区间外会剧烈发散
    /// （实测：纯铂外推到 900 °C 得出 8648 MPa 的荒谬值），故区间外一律返回 NaN。
    /// </summary>
    public double CreepTMinC = double.NaN, CreepTMaxC = double.NaN;

    /// <summary>拟合区间是否为实测数据范围（false = 推定，需向供应商确认）</summary>
    public bool RangeConfirmed;

    public bool InCreepRange(double tC)
        => HasCreep && tC >= CreepTMinC - 1e-9 && tC <= CreepTMaxC + 1e-9;

    /// <summary>电阻率 [Ω·m]</summary>
    public double ResistivityOhmM(double tC)
    {
        double d = tC - T0C;
        return Rho0MicroOhmCm * 1e-8 * (1 + Alpha * d + Beta * d * d);
    }

    private static double Poly5(double[] c, double tK)
        => c[0] * Math.Pow(tK, 5) + c[1] * Math.Pow(tK, 4) + c[2] * Math.Pow(tK, 3)
         + c[3] * tK * tK + c[4] * tK + c[5];

    public double A(double tC) => Poly5(Ca, tC + 273.15);
    public double B(double tC) => Poly5(Cb, tC + 273.15);

    /// <summary>给定寿命，求断裂强度 [MPa]</summary>
    public double RuptureStressMPa(double tC, double hours)
    {
        if (!InCreepRange(tC)) return double.NaN;
        double tK = tC + 273.15;
        return Math.Pow(10.0, (A(tC) * Math.Log10(Math.Max(1e-6, hours)) + B(tC)) / tK);
    }

    /// <summary>给定应力，求寿命 [h]</summary>
    public double RuptureLifeHours(double tC, double sigmaMPa)
    {
        if (!InCreepRange(tC) || sigmaMPa <= 0) return double.NaN;
        double tK = tC + 273.15;
        double a = A(tC);
        if (Math.Abs(a) < 1e-12) return double.NaN;
        return Math.Pow(10.0, (tK * Math.Log10(sigmaMPa) - B(tC)) / a);
    }

    public double AllowableMPa(double tC, double hours, double sf)
        => RuptureStressMPa(tC, hours) / Math.Max(1e-6, sf);
}

/// <summary>
/// 让参数表里的「铂材牌号」变成**下拉**，而不是可以随便打字的文本框（2026-08-24）。
///
/// 为什么要有它：`GradeName` 是个普通 string，PropertyGrid 默认渲染成自由文本框。
/// 打错一个字，整线解会一直跑到 `LineRunner.Judge` 才在 `MaterialDb.Get` 上崩 ——
/// 一次分钟级求解白跑，而报出来的话（现在虽然说清楚了）仍是**事后**的。
/// 下拉是**从源头**堵：选不出材料库里没有的名字。
///
/// ⚠ 两层都要留着，不是重复：
///   · 下拉挡的是**人在界面上打字**；
///   · `MaterialDb.Get` 的明确异常挡的是**方案档里存着旧牌号名**被反序列化进来
///     —— 那条路根本不经过 TypeConverter。
/// </summary>
public sealed class GradeNameConverter : System.ComponentModel.StringConverter
{
    public override bool GetStandardValuesSupported(System.ComponentModel.ITypeDescriptorContext? c) => true;
    /// <summary>true = 只能选、不能打字。这正是本类存在的理由。</summary>
    public override bool GetStandardValuesExclusive(System.ComponentModel.ITypeDescriptorContext? c) => true;

    /// <summary>
    /// ★ 2026-09-18，Opus 5：**可选集合 = 四类数据齐全的那些**（<see cref="GradeChoices.Selectable"/>），
    /// 不再是材料库全集。下拉里**全集照列**（数据不全的灰显、行末写明缺什么）——
    /// 那一层在 <c>PtOptimize.UI.GradeNameEditor</c>；这里守的是「能不能真的落到 GradeName 上」。
    /// ⚠ 名单不写死在这里：唯一来源是 <see cref="MaterialDb.DataCompleteness"/>。
    /// </summary>
    public override System.ComponentModel.TypeConverter.StandardValuesCollection GetStandardValues(
        System.ComponentModel.ITypeDescriptorContext? c)
        => new(GradeChoices.Selectable());

    /// <summary>
    /// 打字／粘贴进来的名字也要过齐全度这一关（下拉之外的那条路）。
    /// ⚠ 方案档反序列化不经过 TypeConverter，那条路仍由 <see cref="MaterialDb.Get"/> 的明确异常守。
    /// </summary>
    public override bool IsValid(System.ComponentModel.ITypeDescriptorContext? c, object? value)
        => value is string s && GradeChoices.IsSelectable(s);
}

public static class MaterialDb
{
    private static PtGrade G(string name, double a, double b, double r0, double t0)
        => new() { Name = name, Alpha = a, Beta = b, Rho0MicroOhmCm = r0, T0C = t0,
                   HasResistivity = true, ResistivityFrom = name };

    public static readonly Dictionary<string, PtGrade> All = new();

    static MaterialDb()
    {
        // ── 电阻率（鉑金電氣計算.xlsx，R23–R44）
        Add(G("Pt",              0.0039678411655333,   -5.849309909955442e-07, 9.83, 0));
        Add(G("FKS16/Pt",        0.0035292752903631386,-4.428617180603556e-07, 10.0, 0));
        Add(G("Pt-Rh/90-10",     0.0013483449502268749,-1.2572651570641538e-08,19.1, 0));
        Add(G("Pt-Rh/80-20",     0.0012512196262706044,-1.286233111334061e-07, 20.8, 20));
        Add(G("FKS16/PtRh-9010", 0.0015918684719033662,-1.978599242696283e-07, 19.9, 0));
        Add(G("Pd",              0.003911619516849052, -1.0293953814147924e-06, 9.73, 0));
        Add(G("Ni",              0.00788080573166809,  -2.9468048461466624e-06, 8.5,  0));
        Add(G("Cu",              0,                     0,                      1.7,  0));

        // ── 蠕变（鉑金材料蠕變應力壽命估算.xlsx，R118–R137）
        //    映射到对应的电阻率牌号；Tanaka 与 Umicore 是不同供应商的同类材料
        Creep("Pt",              "Tanaka-Pure Pt",
              new[]{0d,0,-2.370299e-05,0.1117253,-174.9494,90634.68},
              new[]{0d,0,-3.693292e-06,0.006310574,4.564704,-6958.314});
        Creep("Pt-Rh/90-10",     "Tanaka-PtRh 10",
              new[]{0d,0,4.582428e-06,-0.02175908,34.05233,-18011.13},
              new[]{0d,0,-1.919538e-05,0.08928093,-138.8957,74470});
        Creep("Pt-Rh/80-20",     "Tanaka-PtRh 20",
              new[]{0d,0,-6.227693e-06,0.02709531,-39.55556,18921.05},
              new[]{0d,0,-9.688009e-06,0.04583599,-72.67801,41111.59});
        Creep("FKS16/Pt",        "Umicore-FKS-Rigilit-Pt",
              new[]{2.762393e-11,-2.129091e-07,0.0006547531,-1.004379,767.7714,-233817.3},
              new[]{6.626563e-11,-5.000602e-07,0.001505653,-2.262139,1697.076,-506558.1});
        Creep("FKS16/PtRh-9010", "Umicore-FKS-Rigilit-PtRh10",
              new[]{-3.004845e-11,2.255412e-07,-0.0006739903,1.002341,-742.494,219352.3},
              new[]{1.900681e-10,-1.436176e-06,0.004323662,-6.480952,4836.1,-1434373});

        // 仅有蠕变、无独立电阻率的牌号：借用同族电阻率
        AddCreepOnly("Tanaka-ZGS-Pt", "Pt",
              new[]{1.697763e-10,-1.318982e-06,0.004079551,-6.279521,4810.142,-1466926},
              new[]{4.061288e-10,-3.032907e-06,0.009029327,-13.398,9911.02,-2922593});
        AddCreepOnly("Tanaka-ZGS-PtRh10", "Pt-Rh/90-10",
              new[]{-3.111816e-11,2.288748e-07,-0.0006704143,0.9772903,-709.4058,205194.8},
              new[]{4.41969e-10,-3.365265e-06,0.01020825,-15.42235,11606.88,-3479632});
        AddCreepOnly("Umicore-PtRh10", "Pt-Rh/90-10",
              new[]{4.507634e-11,-3.420564e-07,0.001035489,-1.563124,1176.145,-352987.5},
              new[]{-2.854233e-11,2.232973e-07,-0.0006965167,1.081183,-834.7865,258665.6});
        AddCreepOnly("Umicore-PtRh20", "Pt-Rh/80-20",
              new[]{1.210599e-11,-8.900117e-08,0.000260411,-0.3792111,274.515,-79158.71},
              new[]{3.585565e-11,-2.745654e-07,0.0008381314,-1.276251,969.9115,-291844.3});

        // ── 蠕变拟合的有效温度区间（来自工作簿原始数据点的温度范围）
        //    Tanaka 系列有原始数据可查；Umicore 系列工作簿只给了系数，
        //    区间为推定值，须向供应商确认（RangeConfirmed = false）。
        Range("Pt",                1100, 1400, true);   // Tanaka-Pure Pt 原始数据
        Range("Pt-Rh/90-10",       1100, 1400, true);   // Tanaka-PtRh10
        Range("Pt-Rh/80-20",       1100, 1400, true);   // Tanaka-PtRh20
        Range("Tanaka-ZGS-Pt",     1000, 1500, true);
        Range("Tanaka-ZGS-PtRh10", 1000, 1500, true);
        Range("FKS16/Pt",          1000, 1500, false);  // Umicore，区间推定
        Range("FKS16/PtRh-9010",   1000, 1500, false);
        Range("Umicore-PtRh10",    1000, 1500, false);
        Range("Umicore-PtRh20",    1000, 1500, false);

        SetCompositions();
        SetThermal();
        SetResistivityCaveats();
        SetPrices();
    }

    /// <summary>
    /// 逐牌号登记**名义成分**（2026-09-18，Opus 5）。新加牌号必须在这里显式出现一次，否则抛。
    /// 用途只有一个：判「借用算不算自己的数据」—— 见 <see cref="DataCompleteness"/>。
    /// </summary>
    private static void SetCompositions()
    {
        var comp = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Pt"] = "Pt",                     // 纯铂
            ["FKS16/Pt"] = "Pt",               // Umicore 弥散强化纯铂
            ["Tanaka-ZGS-Pt"] = "Pt",          // Tanaka 氧化锆弥散强化纯铂
            ["Pt-Rh/90-10"] = "Pt-10Rh",
            ["FKS16/PtRh-9010"] = "Pt-10Rh",
            ["Tanaka-ZGS-PtRh10"] = "Pt-10Rh",
            ["Umicore-PtRh10"] = "Pt-10Rh",
            ["Pt-Rh/80-20"] = "Pt-20Rh",
            ["Umicore-PtRh20"] = "Pt-20Rh",
            ["Pd"] = "Pd", ["Ni"] = "Ni", ["Cu"] = "Cu",
        };
        foreach (var g in All.Values)
            g.Composition = comp.TryGetValue(g.Name, out var c) ? c
                : throw new KeyNotFoundException($"牌号「{g.Name}」没有登记名义成分 —— 新加牌号必须在 MaterialDb.SetCompositions 里显式登记");
    }

    // ── Pt-10%Rh 的热导率：Heraeus 材料数据库 PtRh10 页「Thermal」栏图 Thermal conductivity - HT
    //    页脚 Sources Thermal 的脚注 1 = measured and interpolated（实测并内插）。
    //    值是从该页 Highcharts 折线的 SVG 路径数字化回算的（页面文字里没有数值表），
    //    三项标定自检（横轴落在整百、同法反算同页电阻率落在 0.01 整格、常温值与同页 IACS 同量级）见旁证 B 第 6 小节「数字化方法与它的误差」。
    private static readonly double[] PtRh10KTempsC =
        { 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400 };
    private static readonly double[] PtRh10KWmK =
        { 42.5, 45.3, 48.8, 51.0, 53.5, 56.0, 59.0, 61.8, 63.4, 66.0, 68.0, 70.6, 70.7, 70.8 };

    // ── Pt-10%Rh 的比热：同页图 Specific Heat - HT，脚注 2 = measured and extrapolated（实测并外推）。
    //    原图纵轴单位 J/(g·K)，**数据本身只到 0.01 J/(g·K) = 10 J/(kg·K)**，所以是台阶，不是平滑曲线。
    private static readonly double[] PtRh10CpTempsC =
        { 25, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400, 1500 };
    private static readonly double[] PtRh10CpJKgK =
        { 140, 140, 140, 140, 140, 140, 140, 150, 150, 150, 150, 160, 160, 170, 170, 180 };

    private const string HeraeusPtRh10Url =
        "https://www.heraeus-precious-metals.com/en/products-solutions/metal/material-database/md-ps-detail/PtRh10/";

    /// <summary>
    /// 热导率 k(T) 与比热 cp(T) 入库（2026-09-18，Opus 5）。用户 2026-09-17/18 原话：
    /// 「铂铑 10/20 的热导率和比热：上网找带出处的数，找不到就把两个铂铑牌号也灰显直到有数」。
    ///
    /// 查证结果（旁证 B，2026-09-18）：
    ///   · **Pt-10%Rh 有**完整温度函数（Heraeus 材料库 HT 图：k 100–1400 °C 共 14 点、cp 25–1500 °C 共 16 点）；
    ///   · **Pt-20%Rh 一个实测点都没有** —— Heraeus PtRh20 页的 Thermal 栏只有熔化区间；
    ///     JM Tech. Rev. 2005/1962/1961/2023 四篇、Touloukian TPRC Vol.1（未 OCR）、各供应商页逐条查过都没有温度函数。
    ///     按项目规矩「找不到就写找不到」⇒ 不入库，只存 Wiedemann–Franz 推算当**参考值**（<see cref="PtGrade.ThermalKAdvisory"/>）。
    ///
    /// 纯铂两条指回 <see cref="Materials.PtThermalK"/>／<see cref="Materials.PtCp"/>（牌号为纯铂时求解链经 <see cref="PtProps"/> 调的就是它们），
    /// 表里放的是**拟合／核对所用的原始点**，不另立一份数值来源。
    ///
    /// 借用（同名义成分，随值带出、报告点名）：Tanaka-ZGS-Pt ← Pt；Tanaka-ZGS-PtRh10 与 Umicore-PtRh10 ← Pt-Rh/90-10。
    /// FKS16 两个牌号**不借**：与热膨胀那边同一条保守口径（借同基体还是借同为弥散强化的 ZGS 是工程判断，见 ExpansionBorrowOption）。
    /// </summary>
    private static void SetThermal()
    {
        var ptK = new PtPropCurve
        {
            Quantity = "热导率", Unit = "W/(m·K)", FromTable = false, Fit = Materials.PtThermalK,
            TC = Materials.KRefTempsC, V = Materials.KRefKWmK,
            Source = "Kaye & Laby（NPL 网络版）第 2.3.7 小节 Thermal conductivities of metallic elements 纯铂行"
                   + "（273.2/373.2/573.2/973.2 K = 72/72/73/78 W/(m·K)）"
                   + "https://web.archive.org/web/2016/http://www.kayelaby.npl.co.uk/general_physics/2_3/2_3_7.html"
                   + "；APP 用的是 Materials.PtThermalK 的直线（Touloukian/TPRC 推荐值那一支）",
            Note = "0–700 °C 与核对点一致到 ≤2.5 %；700 °C 以上是**推荐值外推，无本项目实测**；"
                 + "带宽 ±8 %（物理把关人 2026-09-18 定稿：1150 °C 处 77.1…90.5，覆盖下沿 Wiedemann–Franz 的 78 与上沿 90；"
                 + "Terada 2005 激光闪射的 95 只作上沿不采用）",
        };
        var ptCp = new PtPropCurve
        {
            Quantity = "比热", Unit = "J/(kg·K)", FromTable = false, Fit = Materials.PtCp,
            TC = Materials.CpFitTempsC, V = Materials.CpFitCpJKgK,
            Source = "Kaye & Laby（NPL 网络版）第 2.3.6 小节 Specific heat capacities of metals, alloys… 纯铂行"
                   + "（273/373/573/773 K = 132/135/141/146 J/(kg·K)，按 0/100/300/500 °C 取）"
                   + "https://web.archive.org/web/2016/http://www.kayelaby.npl.co.uk/general_physics/2_3/2_3_6.html",
            Note = "四点最小二乘直线，最大残差 0.39 J/(kg·K)（0.28 %）；500 °C 以上是外推；"
                 + "2026-09-18 由旧式 133.0+0.0135·T 改来（旧式斜率只有文献的一半、仓库里查不到出处）",
        };
        var rh10K = new PtPropCurve
        {
            Quantity = "热导率", Unit = "W/(m·K)", FromTable = true,
            TC = PtRh10KTempsC, V = PtRh10KWmK,
            Source = "Heraeus Precious Metals 材料数据库 PtRh10 页，Thermal 栏图 Thermal conductivity - HT"
                   + "（页脚 Sources Thermal 脚注 1 = measured and interpolated）；" + HeraeusPtRh10Url
                   + "；2026-09-18 读，值由该页 Highcharts 折线 SVG 数字化回算，不是厂方给的数字表",
            Note = "实测+内插；分辨率约 ±0.1 W/(m·K)（图本身的数据分辨率）；1200 °C 以上曲线走平（70.6→70.7→70.8），"
                 + "所以 1400 °C 以上保端点值、不外插；同页文字的常温值 30 与本曲线 100 °C 的 42.5 不连续（差 40 %），"
                 + "第三方常温值又给 38，**常温段不可用**；高温段旁证：Materials (Basel), 2022, 15(21), 7832, Table 1 取 70.05",
        };
        var rh10Cp = new PtPropCurve
        {
            Quantity = "比热", Unit = "J/(kg·K)", FromTable = true,
            TC = PtRh10CpTempsC, V = PtRh10CpJKgK,
            Source = "Heraeus Precious Metals 材料数据库 PtRh10 页，Thermal 栏图 Specific Heat - HT"
                   + "（页脚 Sources Thermal 脚注 2 = measured and extrapolated）；" + HeraeusPtRh10Url
                   + "；2026-09-18 读，值由该页 Highcharts 折线 SVG 数字化回算",
            Note = "实测+外推；**分辨率只有 ±10 J/(kg·K)（约 ±7 %）**—— 原图纵轴单位 J/(g·K)、数据只到 0.01，"
                 + "所以表里是台阶不是平滑曲线；要拿它做热容判据先问这个精度够不够；"
                 + "Neumann–Kopp 交叉验证在两端合、300 °C 差 +10 %",
        };

        void Own(string grade, PtPropCurve k, PtPropCurve cp)
        {
            var g = All[grade];
            g.ThermalKTable = k; g.CpTable = cp; g.ThermalFrom = grade;
        }
        void Borrow(string grade, string from)
        {
            var src = All[from];
            var g = All[grade];
            g.ThermalKTable = src.ThermalKTable; g.CpTable = src.CpTable; g.ThermalFrom = from;
        }

        Own("Pt", ptK, ptCp);
        Own("Pt-Rh/90-10", rh10K, rh10Cp);
        Borrow("Tanaka-ZGS-Pt", "Pt");
        Borrow("Tanaka-ZGS-PtRh10", "Pt-Rh/90-10");
        Borrow("Umicore-PtRh10", "Pt-Rh/90-10");

        // ── Pt-20%Rh：**没有实测**，只存推算参考值（不入判定、求解链不读）
        var wf = new double[PtRh10KTempsC.Length];
        for (int i = 0; i < PtRh10KTempsC.Length; i++)
        {
            double t = PtRh10KTempsC[i];
            double rho = All["Pt-Rh/80-20"].ResistivityOhmM(t);          // Ω·m
            wf[i] = 0.94 * 2.44e-8 * (t + 273.15) / rho;                 // W/(m·K)
        }
        All["Pt-Rh/80-20"].ThermalKAdvisory = new PtPropCurve
        {
            Quantity = "热导率（推算参考值，不入判定）", Unit = "W/(m·K)", FromTable = true,
            TC = PtRh10KTempsC, V = wf,
            Source = "Wiedemann–Franz：k = L·T/ρ(T)，L = 2.44e-8 W·Ω·K⁻²，ρ 取本牌号工作簿拟合"
                   + "（鉑金電氣計算.xlsx R23–R44）；经验校正 ×0.94 来自同一套算法套到 Pt-10%Rh 上"
                   + "与 Heraeus 实测逐点比的系统偏差（平均 +6 %，最大 +10.5 % 在 400–600 °C，旁证 B 第 4.1 小节「Wiedemann–Franz 推算」）",
            Note = "**推算，非实测，不入数据齐全度判定，求解链不读**。两条已知的不确定："
                 + "① 本牌号电阻率拟合本身待核（见 ResistivityCaveat）；"
                 + "② Heraeus 的 PtRh10 与 PtRh20 两条电阻率曲线几乎重合，与工作簿互不支持（未解决）",
        };
        All["Umicore-PtRh20"].ThermalKAdvisory = All["Pt-Rh/80-20"].ThermalKAdvisory;
    }

    /// <summary>
    /// 电阻率拟合本身的存疑之处（2026-09-18，Opus 5；**只注记，不裁决、不改数**）。
    /// </summary>
    private static void SetResistivityCaveats()
    {
        const string caveat =
            "拟合待核（2026-09-18，Opus 5 记，不裁决）：① 二次项 −1.286e-7 是 Pt-Rh/90-10 的 −1.257e-8 的 10 倍；"
          + "② T₀ = 20 °C，材料库其余牌号都是 0 °C；"
          + "③ Heraeus 的 PtRh10 与 PtRh20 两页电阻率 HT 曲线几乎重合（0.21/0.23/0.26…0.61 与 0.21/0.24/0.26…0.61 Ω·mm²/m），"
          + "而本拟合在 1500 °C 给 PtRh20（53.5 µΩ·cm）**低于** PtRh10（57.2 µΩ·cm）—— 两份数据互不支持；"
          + "物理上 Rh 含量高的一支电阻率不应更低。使用时带 ±5 % 带宽。出处：旁证 B 第 4.1 小节「Wiedemann–Franz 推算」（2026-09-18）";
        foreach (string n in new[] { "Pt-Rh/80-20", "Umicore-PtRh20" })
        {
            All[n].ResistivityCaveat = caveat;
            All[n].ResistivityBandFrac = 0.05;
        }
    }

    private static void Range(string name, double lo, double hi, bool confirmed)
    {
        if (!All.TryGetValue(name, out var g)) return;
        g.CreepTMinC = lo; g.CreepTMaxC = hi; g.RangeConfirmed = confirmed;
    }

    private static void Add(PtGrade g) => All[g.Name] = g;

    private static void Creep(string key, string src, double[] ca, double[] cb)
    {
        var g = All[key];
        g.Ca = ca; g.Cb = cb; g.HasCreep = true; g.CreepSource = src;
    }

    private static void AddCreepOnly(string name, string rhoFrom, double[] ca, double[] cb)
    {
        var baseG = All[rhoFrom];
        All[name] = new PtGrade
        {
            Name = name, CreepSource = name,
            Alpha = baseG.Alpha, Beta = baseG.Beta,
            Rho0MicroOhmCm = baseG.Rho0MicroOhmCm, T0C = baseG.T0C,
            HasResistivity = true, ResistivityFrom = rhoFrom,   // 借用标记（2026-09-15，Opus 5）
            Ca = ca, Cb = cb, HasCreep = true
        };
    }

    /// <summary>
    /// 设定密度与价格。价格倍数按金属含量与现货价推算：
    ///   Pt $1731/oz、Rh $8500/oz（Umicore PMM，2026-08-06）→ Rh/Pt = 4.91
    ///   Pt-10Rh = 0.9·Pt + 0.1·Rh = 1.39×；Pt-20Rh = 1.78×
    /// 弥散强化牌号金属价值与基体相同，加工溢价须以供应商报价填入 FabricationPremium。
    /// </summary>
    public static void SetPrices(double rhOverPt = 4.91)
    {
        void S(string n, double dens, double rhFrac, double fab = 1.0)
        {
            if (!All.TryGetValue(n, out var g)) return;
            g.DensityKgM3 = dens;
            g.MetalPriceRatio = (1 - rhFrac) + rhFrac * rhOverPt;
            g.FabricationPremium = fab;
        }
        S("Pt", 21450, 0);
        S("FKS16/Pt", 21450, 0);
        S("Tanaka-ZGS-Pt", 21450, 0);
        S("Pt-Rh/90-10", 19970, 0.10);
        S("Umicore-PtRh10", 19970, 0.10);
        S("FKS16/PtRh-9010", 19970, 0.10);
        S("Tanaka-ZGS-PtRh10", 19970, 0.10);
        S("Pt-Rh/80-20", 18740, 0.20);
        S("Umicore-PtRh20", 18740, 0.20);
        S("Pd", 12020, 0); S("Ni", 8908, 0); S("Cu", 8960, 0);
    }

    /// <summary>
    /// 按牌号取材料。**找不到就说清楚是哪个牌号、有哪些可选**，不抛裸的
    /// <c>KeyNotFoundException</c>（2026-08-24 修）。
    ///
    /// 病灶：原式是 <c>All[name]</c>。而 <see cref="DesignInputs.GradeName"/> 在参数表里是
    /// **没有下拉约束的纯文本** —— 用户随手打错一个字，或读进一个存了旧牌号名的方案档，
    /// 整线解就在 <c>LineRunner.Judge</c> 里当场崩掉，抛出来的是
    /// 「The given key 'PtRh10' was not present in the dictionary」——
    /// 这句话既不说是牌号的事，也不说该填什么，而它出现在一次分钟级求解的**末尾**。
    /// </summary>
    public static PtGrade Get(string name)
        => All.TryGetValue(name ?? "", out var g) ? g
           : throw new KeyNotFoundException(
               $"材料库里没有牌号「{name}」。可选：" + string.Join("、", All.Keys)
               + "。（参数表里的「铂材牌号」是自由文本，打错一个字就会走到这里。）");
    public static IEnumerable<PtGrade> WithCreep => All.Values.Where(x => x.HasCreep);

    /// <summary>
    /// 曲线 id → 名义成分（**只**为判「膨胀曲线是不是同成分借来的」；曲线本身见
    /// <see cref="PtThermalExpansion.Curves"/>）。2026-09-18，Opus 5。查不到 ⇒ 当作成分不同（保守）。
    /// </summary>
    private static readonly Dictionary<string, string> CurveComposition = new(StringComparer.Ordinal)
    {
        ["Pt"] = "Pt", ["ZGSPt"] = "Pt",
        ["PtRh10"] = "Pt-10Rh", ["ZGSPtRh10"] = "Pt-10Rh",
        ["PtRh20"] = "Pt-20Rh",
        ["PtRh30"] = "Pt-30Rh", ["PtRh95"] = "Pt-95Rh", ["Rh"] = "Rh", ["PtAu5"] = "Pt-5Au",
    };

    /// <summary>
    /// 借来的数据算不算「自己的」：**同名义成分才算**（口径见 <see cref="DataCompleteness"/>）。
    ///
    /// ⚠ **公开是为了让门喂得进「跨成分借用」** —— 材料库现在 12 个牌号里的借用**全是同成分**，
    /// 于是这条判断的「假」那一侧一次都没被走过：写成「一律算自有」门照样绿。
    /// 同一种空转本项目已经栽过（齐全度合取式少一项）。门 R48ThermalSourceTests 用不存在的组合钉两侧。
    /// </summary>
    /// <param name="fromGrade">数据取自哪个牌号</param>
    /// <param name="toGrade">用这份数据的牌号</param>
    public static bool BorrowedGradeCountsAsOwn(string fromGrade, string toGrade)
        => All.TryGetValue(fromGrade ?? "", out var f) && All.TryGetValue(toGrade ?? "", out var t)
           && !string.IsNullOrEmpty(t.Composition) && f.Composition == t.Composition;

    /// <summary>同上，但数据来源是一条**膨胀曲线**（曲线 → 名义成分见 <see cref="CurveComposition"/>）。</summary>
    public static bool BorrowedCurveCountsAsOwn(string curveId, string toGrade)
        => CurveComposition.TryGetValue(curveId ?? "", out var cc) && All.TryGetValue(toGrade ?? "", out var t)
           && !string.IsNullOrEmpty(t.Composition) && cc == t.Composition;

    /// <summary>
    /// 数据齐全度判定（2026-09-16 立，2026-09-18 第二次改口径，Opus 5）。
    ///
    /// 用户 2026-09-16 原话：「数据不全(电阻/热膨胀/蠕变应力)的铂金合金先以灰色不可选展示，只用数据全的铂金合金」；
    /// 用户 2026-09-17/18 又定：**可选牌号 = Pt、Pt-Rh/90-10、Pt-Rh/80-20、Tanaka-ZGS-Pt、Tanaka-ZGS-PtRh10**，
    /// 且「铂铑 10/20 的热导率和比热：上网找带出处的数，找不到就把两个铂铑牌号也灰显直到有数」。
    /// 本函数只给判定，灰显由界面路做。两处改口径都在这里：
    ///
    /// ① **第四类「热导率／比热」**（2026-09-18 新加）：OwnThermal = 有 k(T) 与 cp(T) 两条曲线，且来源是自己或同名义成分。
    ///    Pt-20%Rh 公开文献一个实测点都查不到（旁证 B，2026-09-18 逐条记录）⇒ Pt-Rh/80-20 与 Umicore-PtRh20 因缺此项灰显。
    ///    Wiedemann–Franz 推算（×0.94）只存为 <see cref="PtGrade.ThermalKAdvisory"/> 参考值，**不入本判定**。
    /// ② **借用口径由「一律不算」放宽为「同名义成分才算」**（2026-09-18）：
    ///    原口径是 2026-09-16 主会话对用户原话的解读（「借用 ≠ 自己的数据」，当时标了「未向用户确认」，
    ///    列为待定 9）。用户 2026-09-17/18 把两个 Tanaka-ZGS 牌号列进可选名单 ⇒ 待定 9 结案：
    ///    **同名义成分的借用视同自有**（差的是弥散相 &lt;1 % 与供应商，不是成分），**跨成分借用仍不算**。
    ///    ⚠ 这一条改动本身让 Tanaka-ZGS-Pt 与 Tanaka-ZGS-PtRh10 由「不齐全」变「齐全」——
    ///    是口径变了，不是数据变了；借了谁仍逐条写在 <see cref="GradeDataCompleteness.Borrowed"/> 里，不许消失。
    ///
    /// 四类定义写死：
    ///   OwnResistivity     = 电阻率取自本牌号自己的工作簿行，或同名义成分牌号的行；
    ///   OwnExpansion       = 膨胀映射 Link == Direct，或 Borrowed 且曲线同名义成分（不带 FKS16 借用开关）；
    ///   OwnCreepWithPoints = 有持久强度系数且拟合区间由原始点确认（HasCreep 且 RangeConfirmed）；
    ///   OwnThermal         = 有热导率与比热两条曲线，且取自自己或同名义成分牌号；
    ///   IsComplete         = 四者皆真（<see cref="Compose"/>）；Missing 逐项点名缺什么（全名成句，界面可直接显示）。
    /// 门：MaterialDbTests（决定记录逐牌号写死；齐全的是 Pt、Pt-Rh/90-10、Tanaka-ZGS-Pt、Tanaka-ZGS-PtRh10）。
    /// </summary>
    public static GradeDataCompleteness DataCompleteness(string gradeName)
    {
        var g = Get(gradeName);
        var m = PtThermalExpansion.Resolve(g.Name);

        var borrowed = new List<string>();
        var missing = new List<string>();

        // ① 电阻率
        bool ownR;
        if (!g.HasResistivity) { ownR = false; missing.Add("电阻率：无数据"); }
        else if (g.ResistivityFrom == g.Name) ownR = true;
        else if (BorrowedGradeCountsAsOwn(g.ResistivityFrom, g.Name))
        { ownR = true; borrowed.Add($"电阻率：借用 {g.ResistivityFrom} 行（同名义成分 {g.Composition}）"); }
        else { ownR = false; missing.Add($"电阻率：借用 {g.ResistivityFrom} 行（名义成分不同），不是本牌号的测试值"); }

        // ② 热膨胀
        bool ownE;
        if (m.Link == ExpansionLink.Direct) ownE = true;
        else if (m.Link == ExpansionLink.Borrowed && m.CurveId is not null
                 && BorrowedCurveCountsAsOwn(m.CurveId, g.Name))
        { ownE = true; borrowed.Add($"热膨胀：借用 {m.CurveId} 曲线（同名义成分 {g.Composition}）"); }
        else if (m.Link == ExpansionLink.Borrowed)
        { ownE = false; missing.Add($"热膨胀：借用 {m.CurveId} 曲线（名义成分不同），不是本牌号的数据"); }
        else { ownE = false; missing.Add("热膨胀：工作簿没有本牌号"); }

        // ③ 持久强度
        bool ownC = g.HasCreep && g.RangeConfirmed;
        if (!ownC) missing.Add(!g.HasCreep ? "持久强度：无数据"
            : $"持久强度：拟合区间 {g.CreepTMinC:0}–{g.CreepTMaxC:0} °C 为推定，工作簿没有原始点");

        // ④ 热导率／比热（2026-09-18 新加）
        bool ownT;
        if (!g.HasThermal)
        { ownT = false; missing.Add("热导率与比热：无数据（2026-09-18 查遍公开来源，本牌号没有带出处的实测温度函数）"); }
        else if (g.ThermalFrom == g.Name) ownT = true;
        else if (BorrowedGradeCountsAsOwn(g.ThermalFrom, g.Name))
        { ownT = true; borrowed.Add($"热导率与比热：借用 {g.ThermalFrom} 的数据（同名义成分 {g.Composition}）"); }
        else { ownT = false; missing.Add($"热导率与比热：借用 {g.ThermalFrom} 的数据（名义成分不同），不是本牌号的"); }

        return Compose(g.Name, ownR, ownE, ownC, ownT, missing, borrowed);
    }

    /// <summary>
    /// 四个判定合成一条结论 —— **单独提出来，为的是门能用人造牌号把四项的每一种真假组合都钉一遍**
    /// （2026-09-18，Opus 5）。上一轮的门只走了库里现有的 12 个牌号，合取式里缺 (真,真,真,假) 这种组合，
    /// 于是「第四类根本没查」这种改法门是绿的 —— 空转。现在 <see cref="Compose"/> 是纯函数，门直接喂 16 种组合。
    /// </summary>
    public static GradeDataCompleteness Compose(string grade, bool ownR, bool ownE, bool ownC, bool ownT,
                                                IEnumerable<string> missing, IEnumerable<string> borrowed)
        => new(grade, ownR, ownE, ownC, ownT, ownR && ownE && ownC && ownT,
               missing.ToArray(), borrowed.ToArray());
}

/// <summary>
/// 一个牌号四份数据的齐全度（2026-09-16 立、2026-09-18 加第四类，Opus 5；定义见 <see cref="MaterialDb.DataCompleteness"/>）。
/// Missing 逐项点名缺什么、Borrowed 逐项点名同名义成分借了谁（都全名成句，不出现代号）；IsComplete = 四个 Own 皆真。
/// </summary>
public sealed record GradeDataCompleteness(
    string Grade, bool OwnResistivity, bool OwnExpansion, bool OwnCreepWithPoints, bool OwnThermal,
    bool IsComplete, string[] Missing, string[] Borrowed);
