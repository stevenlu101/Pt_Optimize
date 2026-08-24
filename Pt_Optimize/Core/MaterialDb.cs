using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 铂系材料数据库 —— 全部来自用户提供的实测工作簿，非文献估算。
///
///   电阻率：鉑金電氣計算.xlsx    ρ(T) = ρ₀·[1 + α(T−T₀) + β(T−T₀)²]   [μΩ·cm]
///   持久强度：鉑金材料蠕變應力壽命估算.xlsx
///             T·log₁₀σ = a(T)·log₁₀t + b(T)，a、b 为 T(K) 的五次多项式
///
/// 该蠕变式比单一 Larson–Miller 直线更灵活：斜率 a 也随温度变，
/// 因而能同时贴合弥散强化材料（曲线平缓）与纯铂（曲线陡）。
/// </summary>
public sealed class PtGrade
{
    public string Name = "";
    public string CreepSource = "";          // 蠕变数据对应的牌号名（可能与电阻率牌号不同）

    // 电阻率
    public double Alpha, Beta, Rho0MicroOhmCm, T0C;
    public bool HasResistivity;

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
    public override System.ComponentModel.TypeConverter.StandardValuesCollection GetStandardValues(
        System.ComponentModel.ITypeDescriptorContext? c)
        => new(MaterialDb.All.Keys.OrderBy(x => x, System.StringComparer.Ordinal).ToArray());
}

public static class MaterialDb
{
    private static PtGrade G(string name, double a, double b, double r0, double t0)
        => new() { Name = name, Alpha = a, Beta = b, Rho0MicroOhmCm = r0, T0C = t0,
                   HasResistivity = true };

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

        SetPrices();
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
            HasResistivity = true, Ca = ca, Cb = cb, HasCreep = true
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
}
