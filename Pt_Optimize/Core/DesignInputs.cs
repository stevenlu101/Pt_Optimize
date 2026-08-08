using System.ComponentModel;

namespace PtOptimize.Core;

public enum SupplyMode
{
    [Description("单相交流（可控矽相控）")] AcPhase,
    [Description("单相交流（过零/周波）")] AcZeroCross,
    [Description("直流（整流）")] Dc
}

public enum Orientation
{
    [Description("水平")] Horizontal,
    [Description("垂直")] Vertical
}

/// <summary>
/// 全部设计输入。绑定到 PropertyGrid，Category 决定分组顺序（用数字前缀排序）。
/// </summary>
public class DesignInputs
{
    // ---------- 1 工艺 ----------
    [Category("1 工艺"), DisplayName("目标金属温度 [°C]"),
     Description("管中段控温点的金属温度设定值")]
    public double TSetC { get; set; } = 1300;

    [Category("1 工艺"), DisplayName("玻璃液相线 T_liq [°C]"),
     Description("析晶判据基准。全程金属温度必须高于 T_liq + 裕度")]
    public double TLiquidusC { get; set; } = 1050;

    [Category("1 工艺"), DisplayName("析晶温度裕度 [K]")]
    public double DevitMarginK { get; set; } = 40;

    [Category("1 工艺"), DisplayName("产量 [t/day]")]
    public double ThroughputTPerDay { get; set; } = 2.0;

    [Category("1 工艺"), DisplayName("玻璃进口温度 [°C]")]
    public double TGlassInC { get; set; } = 1300;

    [Category("1 工艺"), DisplayName("本段玻璃压力水头 [m]"),
     Description("铂管为分段控制，每段的液柱高度不同 —— 按本段实际值填。" +
                 "决定管内压，进而决定环向应力 σθ = p·r/t")]
    public double GlassHeadM { get; set; } = 0.5;

    [Category("1 工艺"), DisplayName("支承跨距 [mm]"),
     Description("铂管两支承点间距。默认取段长（两端法兰即支承点）")]
    public double SupportSpanMm { get; set; } = 300;

    [Category("1 工艺"), DisplayName("铂材牌号"),
     Description("MaterialDb 中的牌号名。电阻率与持久强度均取该牌号的实测数据")]
    public string GradeName { get; set; } = "Pt";

    [Category("1 工艺"), DisplayName("设计寿命 [h]"), Description("1 年 = 8760 h")]
    public double DesignLifeHours { get; set; } = 8760;

    [Category("1 工艺"), DisplayName("力学安全系数")]
    public double SafetyFactor { get; set; } = 2.0;

    [Category("1 工艺"), DisplayName("环境温度 [°C]")]
    public double TAmbC { get; set; } = 25;

    // ---------- 2 供料管几何 ----------
    [Category("2 供料管几何"), DisplayName("内径 ID [mm]")]
    public double TubeIdMm { get; set; } = 50.0;   // Pt_Heater.3dm: Ø52/Ø50

    [Category("2 供料管几何"), DisplayName("段长 L [mm]"), Description("两法兰之间的加热段长度")]
    public double TubeLengthMm { get; set; } = 300;  // Pt_Heater.3dm

    [Category("2 供料管几何"), DisplayName("壁厚 [mm]"),
     Description("校核模式用。设计模式下程序会给出电流密度所需的最小壁厚")]
    public double WallMm { get; set; } = 0.8;

    [Category("2 供料管几何"), DisplayName("最小可制造壁厚 [mm]"),
     Description("工艺/操作下限。设计壁厚 = max(电学所需, 本值)")]
    public double WallMinMm { get; set; } = 1.0;    // Pt_Heater.3dm 实测壁厚

    [Category("2 供料管几何"), DisplayName("安装姿态")]
    public Orientation Posture { get; set; } = Orientation.Horizontal;

    // ---------- 3 电气 ----------
    [Category("3 电气"), DisplayName("许用电流密度 [A/mm²]"),
     Description("纯铂连续 8–10，短时极限 15。按 RMS 计")]
    public double JAllowAPerMm2 { get; set; } = 10.0;

    [Category("3 电气"), DisplayName("二次电源型式")]
    public SupplyMode Supply { get; set; } = SupplyMode.AcPhase;

    [Category("3 电气"), DisplayName("直流偏置 [% of I_rms]"),
     Description("交流下应为 0。反并联可控矽触发角不对称会产生直流分量，" +
                 "该分量驱动玻璃电解 → 铂阳极溶解 + 碱迁移。用钳表直流档实测填入")]
    public double DcOffsetPercent { get; set; } = 0.0;

    [Category("3 电气"), DisplayName("玻璃电阻率 [Ω·cm]"),
     Description("工作温度下的熔体电阻率。含碱玻璃 1–10，硼硅/无碱 50–500。仅用于直流分量核算")]
    public double GlassResistivityOhmCm { get; set; } = 5.0;

    [Category("3 电气"), DisplayName("铂溶解价态 z"),
     Description("法拉第定律用。Pt²⁺ 取 2，Pt⁴⁺ 取 4（保守取 2）")]
    public double PtValence { get; set; } = 2.0;

    // ---------- 4 铂表面 / 保温 ----------
    [Category("4 铂表面与保温"), DisplayName("铂表面发射率 ε"),
     Description("抛光 0.10–0.15，使用后发暗 0.20–0.30。裸管时热损失与本值成正比")]
    public double PtEmissivity { get; set; } = 0.18;

    [Category("4 铂表面与保温"), DisplayName("保温外表面发射率 ε_out"),
     Description("氧化铝纤维/致密氧化铝约 0.4–0.6")]
    public double OuterEmissivity { get; set; } = 0.45;

    [Category("4 铂表面与保温"), DisplayName("① 内层（贴铂）")]
    public InsulationLayer Layer1 { get; set; } = new()
    { Name = "高纯氧化铝纤维", ThicknessMm = 10, K0 = 0.04, K1 = 3.0e-4 };

    [Category("4 铂表面与保温"), DisplayName("② 中层")]
    public InsulationLayer Layer2 { get; set; } = new()
    { Name = "致密氧化铝套管", ThicknessMm = 5, K0 = 25.0, K1 = -0.016 };

    [Category("4 铂表面与保温"), DisplayName("③ 外层（可选）")]
    public InsulationLayer Layer3 { get; set; } = new()
    { Name = "外加保温", ThicknessMm = 0, K0 = 0.05, K1 = 2.5e-4, Enabled = false };

    // ---------- 5 法兰 ----------
    [Category("5 法兰"), DisplayName("法兰内半径 ri [mm]"), Description("= 管外半径")]
    public double FlangeRiMm { get; set; } = 26.0;  // = 管外半径

    [Category("5 法兰"), DisplayName("法兰外半径 ro [mm]"),
     Description("散热面积 ∝ (ro²−ri²)，是二次项，最敏感")]
    public double FlangeRoMm { get; set; } = 50.0;

    [Category("5 法兰"), DisplayName("剖面形状"),
     Description("等厚=长方形剖面；梯形=线性渐变；理想=1/r² 渐变(φ 处处为1)；理想截断=受 t_min 限制的可制造版")]
    public FlangeShape FlangeShapeMode { get; set; } = FlangeShape.Rectangular;

    [Category("5 法兰"), DisplayName("法兰厚度 tf [mm]"),
     Description("等厚模式用。自身发热 ∝ 1/tf —— 越厚越冷，且越费铂")]
    public double FlangeThickMm { get; set; } = 3.0;

    [Category("5 法兰"), DisplayName("梯形 内缘厚 [mm]"), Description("梯形模式：r_i 处厚度")]
    public double FlangeThickInnerMm { get; set; } = 0.8;

    [Category("5 法兰"), DisplayName("梯形 外缘厚 [mm]"), Description("梯形模式：r_o 处厚度")]
    public double FlangeThickOuterMm { get; set; } = 0.4;

    [Category("5 法兰"), DisplayName("最小可制造厚 [mm]"),
     Description("理想渐变的截断下限，同时决定可用外径上限 r_o,max = √(C/t_min)")]
    public double FlangeThickMinMm { get; set; } = 0.4;

    [Category("5 法兰"), DisplayName("法兰有保温"),
     Description("法兰双面包覆高纯氧化铝纤维。降低 q″ 可线性提升自给率 φ")]
    public bool FlangeInsulated { get; set; } = true;

    [Category("5 法兰"), DisplayName("法兰保温厚 [mm]"), Description("单面厚度，材料取内层①的 k(T)")]
    public double FlangeInsulThickMm { get; set; } = 10.0;

    [Category("5 法兰"), DisplayName("法兰吹风风速 [m/s]"),
     Description("压缩空气强制冷却。0 = 仅自然对流。每吹掉一瓦都要由铂金发出来，直接折算成铂重")]
    public double FlangeAirVelocityMPerS { get; set; } = 0.0;

    [Category("5 法兰"), DisplayName("铜排夹水冷温度 [°C]"),
     Description("舌片末端整条边的强制温度。<0 = 无水冷（自由辐射端）。" +
                 "水冷后舌片成为真正的电流引线，其焦耳热可能倒灌回管根（理论模型 §8.11）")]
    public double BusbarClampTempC { get; set; } = -1;

    [Category("5 法兰"), DisplayName("法兰厚度自动定尺"),
     Description("按 J_法兰 ≤ 许用值反算最小厚度。J ∝ 1/t，故 t_req = t·(J_max/J_allow) 一步精确")]
    public bool SizeFlangeThickness { get; set; } = false;

    [Category("5 法兰"), DisplayName("铜排/舌片导热 [W/K]"),
     Description("从法兰导向外部结构的附加导热通道。纯辐射法兰填 0")]
    public double BusbarConductanceWPerK { get; set; } = 0.0;

    // ---------- 6 玻璃物性 ----------
    [Category("6 玻璃物性"), DisplayName("密度 [kg/m³]")]
    public double GlassDensity { get; set; } = 2500;

    [Category("6 玻璃物性"), DisplayName("比热 [J/kg·K]")]
    public double GlassCp { get; set; } = 1300;

    [Category("6 玻璃物性"), DisplayName("内壁换热系数 hg [W/m²K]"),
     Description("层流 Nu≈3.7–4.7，2 t/day、ID40 下约 200–250")]
    public double HGlass { get; set; } = 220;

    [Category("6 玻璃物性"), DisplayName("粘度 [Pa·s]"), Description("压降估算用")]
    public double GlassViscosity { get; set; } = 30;

    [Category("5 法兰"), DisplayName("法兰抽热覆盖 [W]"),
     Description("耦合求解时由二维法兰模型回灌；<0 表示用一维环形模型（已作废，仅兼容旧算例）")]
    [Browsable(false)]
    public double FlangeDrawOverrideW { get; set; } = -1;

    [Category("2 供料管几何"), DisplayName("壁厚由程序反算"),
     Description("关闭 = 校核模式：壁厚取「最小可制造壁厚」的实测值，程序只报实际 J")]
    public bool SizeWall { get; set; } = false;

    // ---------- 7 数值 ----------
    [Category("7 数值"), DisplayName("网格节点数"),
     Description("二阶格式。网格收敛测试显示 n=201 在冷点温度上约 2 K 离散误差，n=401 约 0.5 K")]
    public int Nodes { get; set; } = 401;

    // ---------- 派生 ----------
    [Browsable(false)] public double TubeId => TubeIdMm * 1e-3;
    [Browsable(false)] public double TubeLength => TubeLengthMm * 1e-3;
    [Browsable(false)] public double Wall => WallMm * 1e-3;
    [Browsable(false)] public double JAllow => JAllowAPerMm2 * 1e6;   // A/m²
    [Browsable(false)] public double GlassRho => GlassResistivityOhmCm * 1e-2;  // Ω·m
    [Browsable(false)] public double MassFlow => ThroughputTPerDay * 1000.0 / 86400.0; // kg/s

    [Browsable(false)]
    public List<InsulationLayer> Layers => new() { Layer1, Layer2, Layer3 };
}
