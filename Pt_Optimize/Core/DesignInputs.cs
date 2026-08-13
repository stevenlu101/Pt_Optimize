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
    public double ThroughputTPerDay { get; set; } = 1.5;   // 现场实测值

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

    // ★ HANDOVER §4.2l：用户「稳态电流密度远低于 10 A/mm²」，而模型算出管 J≈10.3 顶在限值上；
    //   同一根因还造成玻璃温降 模型 41 K vs 实测 20 K。散热高估 ⇒ 功率大 ⇒ 电流大 ⇒ J 大、抽热大。
    //   本系数是**集总标定量**，不预设高估来自哪一环（发射率？纤维实际更厚？环境不是 25 °C
    //   而是被炉膛包围？氧化铝托管接触？）—— 那些只有现场才能分辨。
    //   实现上对「层导热 + 表面换热」**同倍**缩放，于是各界面温度不变而热流严格 ×LossScale
    //   （只缩表面在纤维热阻主导时几乎无效；只缩最终结果则内外能量不闭合）。
    //   标定方法见 CLI --calib。默认 1.0 = 不标定，与历史结果一致。
    [Category("4 铂表面与保温"), DisplayName("散热标定系数"),
     Description("整条散热通道的集总标定倍率。1.0 = 模型原值；0.5 = 实际散热只有模型的一半。\n" +
                 "由 --calib 用实测段电流或实测玻璃温降反标定。见 HANDOVER §4.2l")]
    public double LossScale { get; set; } = 1.0;

    // ★ 2026-08-10 按现场实况修正：纤维包覆厚度 **2–3 mm**（原设 10 mm，差 4 倍）。
    //   保温热阻几乎全部由纤维贡献（致密氧化铝 k≈9 W/m·K，其 5 mm 只占总热阻约 1 %），
    //   故这一项直接决定散热量级 —— 改动会连带影响电功率、法兰自给率 Φ 与升温核算。
    [Category("4 铂表面与保温"), DisplayName("① 内层（贴铂）"),
     Description("现场实测包覆厚度 2–3 mm，取中值 2.5。热阻几乎全在这一层")]
    public InsulationLayer Layer1 { get; set; } = new()
    { Name = "高纯氧化铝纤维", ThicknessMm = 2.5, K0 = 0.04, K1 = 3.0e-4 };

    // ⚠ 实物是**半管套**（仅下半圈，见 Pt_Heater.3dm 图层「氧化铝管」，内 R26/外 R31，Z≤0）。
    //   此处仍按整圈同心层处理 —— 因其热阻只占约 1 %，影响主要在外表面半径与发射率，
    //   量级上可接受；若要精确需改为按包角加权的并联热阻。
    [Category("4 铂表面与保温"), DisplayName("② 中层"),
     Description("致密氧化铝半管套（实物仅下半圈）。热阻占比 ~1%，此处按整圈近似")]
    public InsulationLayer Layer2 { get; set; } = new()
    { Name = "致密氧化铝半管套", ThicknessMm = 5, K0 = 25.0, K1 = -0.016 };

    [Category("4 铂表面与保温"), DisplayName("③ 外层（可选）")]
    public InsulationLayer Layer3 { get; set; } = new()
    { Name = "外加保温", ThicknessMm = 0, K0 = 0.05, K1 = 2.5e-4, Enabled = false };

    // ---------- 5 法兰 ----------
    //
    // ★ 2026-08-12 大清理：法兰的**几何**（盘径、厚度、剖面形状、渐变）已全部移到
    //   FlangePlate / .3dm 那条路上（壳网格 → FV 电流场 → FV 温度场）。
    //   原先挂在这里的 FlangeRiMm / FlangeRoMm / FlangeShapeMode / FlangeThickMm /
    //   FlangeThickInnerMm / FlangeThickOuterMm / FlangeThickMinMm / SizeFlangeThickness /
    //   BusbarConductanceWPerK **只服务于已作废的一维环形模型 FlangeRadial**，
    //   随该模型一并删除（HANDOVER §5、§7）。
    //   本节现在只留**与几何无关的物理边界**：保温、吹风、夹持。

    [Category("5 法兰"), DisplayName("法兰有保温"),
     Description("法兰双面包覆高纯氧化铝纤维。降低 q″ 会降低自给所需的厚度")]
    public bool FlangeInsulated { get; set; } = true;

    // ★ 2026-08-10 随 Layer1 一并改为 2.5：用户给的「纤维包覆 2–3 mm」是针对铂管的，
    //   此处按「同一材料同一工艺」外推到法兰。**待现场确认**（见 HANDOVER §6 待补数据 ⑦）。
    //   留 10 mm 而管子改 2.5 mm 会物理不自洽：管子保温薄 ⇒ 电流大 ⇒ 同一电流流过裹得厚的法兰
    //   ⇒ 法兰过热、Φ≫1 ⇒ 向管根倒灌，实测算出管根 2137 °C（超铂熔点 1768 °C）。
    [Category("5 法兰"), DisplayName("法兰保温厚 [mm]"),
     Description("单面厚度，材料取内层①的 k(T)。按与铂管同一包覆工艺取 2.5，待现场确认")]
    public double FlangeInsulThickMm { get; set; } = 2.5;

    [Category("5 法兰"), DisplayName("法兰吹风风速 [m/s]"),
     Description("压缩空气强制冷却。0 = 仅自然对流。每吹掉一瓦都要由铂金发出来，直接折算成铂重")]
    public double FlangeAirVelocityMPerS { get; set; } = 0.0;

    [Category("5 法兰"), DisplayName("铜排夹持温度 [°C]"),
     Description("舌片末端整条边的强制温度。<0 = 无夹冷（自由辐射端）。" +
                 "空冷即可，不需要水冷 —— 400 °C 与 80 °C 的差别仅约 10 W。" +
                 "★ 这一项是现场把法兰自给率整定到位的**唯一可调旋钮**（HANDOVER §4.2v）")]
    public double BusbarClampTempC { get; set; } = -1;

    [Category("5 法兰"), DisplayName("铜排压接长度 [mm]"),
     Description("沿舌片方向的压接长度，即定温边界的深度。\n" +
                 "★ 早先在网格里硬编码为 3 mm —— 那是**数值边界，不是设计值**：\n" +
                 "  3 mm × 舌宽 40 mm = 120 mm² 接触面，共用片 1099 A ⇒ 界面电流密度约 9 A/mm²，\n" +
                 "  而铜排压接通常按 ≤1 A/mm² 量级设计，差一个数量级 —— 现场做不出来。\n" +
                 "加长它会把定温边界推向圆盘、缩短导热路径 ⇒ 铜排带走的热增加，**必须重算**。")]
    public double BusbarClampLengthMm { get; set; } = 3.0;

    // ---------- 6 玻璃物性 ----------
    [Category("6 玻璃物性"), DisplayName("密度 [kg/m³]")]
    public double GlassDensity { get; set; } = 2500;

    [Category("6 玻璃物性"), DisplayName("比热 [J/kg·K]")]
    public double GlassCp { get; set; } = 1300;

    [Category("6 玻璃物性"), DisplayName("内壁换热系数 hg [W/m²K]"),
     Description("由层流 Nu≈3.66 与玻璃熔体导热 k≈0.9 W/m·K 得 hg = Nu·k/D ≈ 65（ID50）。\n" +
                 "旧默认值 220 对应 k_eff≈2.2，远高于熔体导热，会把玻璃向管壁的放热放大约 3.4 倍，\n" +
                 "使 --glass 的全程温降算成 67.6 K 而实测仅 20 K。改动依据见 --glass 的反解。")]
    public double HGlass { get; set; } = 65;

    [Category("6 玻璃物性"), DisplayName("粘度 [Pa·s]"), Description("压降估算用")]
    public double GlassViscosity { get; set; } = 30;

    [Category("5 法兰"), DisplayName("法兰抽热覆盖 [W]"),
     Description("耦合求解时由二维法兰模型回灌。是否生效由 FlangeDrawOverrideSet 决定，不看本值符号。")]
    [Browsable(false)]
    public double FlangeDrawOverrideW { get; set; }

    /// <summary>
    /// 上面那个覆盖值是否有效。
    ///
    /// ★ 不能用「负数」或「NaN」当哨兵：
    ///   · 负数不行 —— 法兰自给率 Φ&gt;1 时自身焦耳热有余，会向管子**倒灌**热量，D 为负是合法值。
    ///     旧代码 `FlangeDrawOverrideW >= 0` 使 Φ&gt;1 的算例静默回退到已作废的一维模型。
    ///   · NaN 也不行 —— <see cref="SegmentSolver.Clone"/> 与算例存盘都走 JSON，
    ///     而 JSON 规范不允许 NaN，序列化直接抛 ArgumentException。
    /// 故用显式布尔。
    /// </summary>
    [Browsable(false)]
    public bool FlangeDrawOverrideSet { get; set; }

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
