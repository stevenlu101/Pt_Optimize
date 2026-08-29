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
    [Category("8 ✗ 被页面/设计记录接管（改了对整线链没用）"), DisplayName("目标金属温度 [°C]"),
     Description("⚠ 本项被「③ 整线核算」页的分段控温点表接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　管中段控温点的金属温度设定值")]
    public double TSetC { get; set; } = 1300;

    [Category("1 A·B 粗算 — 工艺"), DisplayName("玻璃液相线 T_liq [°C]"),
     Description("析晶判据基准。全程金属温度必须高于 T_liq + 裕度")]
    public double TLiquidusC { get; set; } = 1050;

    [Category("1 A·B 粗算 — 工艺"), DisplayName("析晶温度裕度 [K]")]
    public double DevitMarginK { get; set; } = 40;

    [Category("1 A·B 粗算 — 工艺"), DisplayName("产量 [t/day]")]
    public double ThroughputTPerDay { get; set; } = 1.5;   // 现场实测值

    [Category("1 A·B 粗算 — 工艺"), DisplayName("玻璃进口温度 [°C]")]
    public double TGlassInC { get; set; } = 1300;

    [Category("8 ✗ 被页面/设计记录接管（改了对整线链没用）"), DisplayName("本段玻璃压力水头 [m]"),
     Description("⚠ 本项被「③ 整线核算」页的分段水头接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　铂管为分段控制，每段的液柱高度不同 —— 按本段实际值填。" +
                 "决定管内压，进而决定环向应力 σθ = p·r/t")]
    public double GlassHeadM { get; set; } = 0.5;

    [Category("1 A·B 粗算 — 工艺"), DisplayName("支承跨距 [mm]"),
     Description("铂管两支承点间距。默认取段长（两端法兰即支承点）")]
    public double SupportSpanMm { get; set; } = 300;

    /// <summary>
    /// ⚠ <see cref="GradeNameConverter"/> 把它做成**只能选的下拉** —— 见那里的说明：
    ///   自由文本时打错一个字，整线解会跑到最后才在 MaterialDb.Get 上崩。
    /// </summary>
    [Category("5 C 整线 — 管几何"), DisplayName("铂材牌号"),
     TypeConverter(typeof(GradeNameConverter)),
     Description("MaterialDb 中的牌号名（**只能从下拉里选**，打不了字）。" +
                 "电阻率与持久强度均取该牌号的实测数据")]
    public string GradeName { get; set; } = "Pt";

    [Category("1 A·B 粗算 — 工艺"), DisplayName("设计寿命 [h]"), Description("1 年 = 8760 h")]
    public double DesignLifeHours { get; set; } = 8760;

    [Category("1 A·B 粗算 — 工艺"), DisplayName("力学安全系数")]
    public double SafetyFactor { get; set; } = 2.0;

    [Category("3 A·B·C 共用 — 保温与表面"), DisplayName("环境温度 [°C]")]
    public double TAmbC { get; set; } = 25;

    // ---------- 2 供料管几何 ----------
    [Category("5 C 整线 — 管几何"), DisplayName("内径 ID [mm]")]
    public double TubeIdMm { get; set; } = 50.0;   // Pt_Heater.3dm: Ø52/Ø50

    [Category("5 C 整线 — 管几何"), DisplayName("段长 L [mm]"), Description("两法兰之间的加热段长度")]
    public double TubeLengthMm { get; set; } = 300;  // Pt_Heater.3dm

    [Category("9 ✗ 对整线链无效"), DisplayName("壁厚 [mm]"),
     Description("⚠ 本项被整线链只用来自 ③ 页控件的 LineCase.WallMm；本项只影响「② 粗算」的单段解接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　校核模式用。设计模式下程序会给出电流密度所需的最小壁厚")]
    public double WallMm { get; set; } = 0.8;

    [Category("8 ✗ 被页面/设计记录接管（改了对整线链没用）"), DisplayName("最小可制造壁厚 [mm]"),
     Description("⚠ 本项被「③ 整线核算」页的「壁厚 mm」控件接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　工艺/操作下限。设计壁厚 = max(电学所需, 本值)")]
    public double WallMinMm { get; set; } = 1.0;    // Pt_Heater.3dm 实测壁厚

        /// <summary>
    /// 焊接工艺最小厚度的**内置默认值** [mm]。
    ///
    /// 用户 2026-08-21：「0.6 是默认值不随牌号，只需标明默认或是手动[实际经验]」。
    /// 常数放这里是为了让 <see cref="WeldMinSource"/> 有一个可比的基准 ——
    /// 判「这个数是内置的还是人填的」只能跟它比。
    /// </summary>
    public const double WeldMinDefaultMm = 0.6;

    /// <summary>
    /// 这条下界是**默认值**还是**手动（实际经验）**。
    ///
    /// ★ 故意**不做成勾选框**：多一个开关就多一个「值改了、开关忘了改」的机会，
    ///   而那正是本项目最忌的「同一件事存两处然后悄悄漂开」。
    ///   直接拿值本身判 —— 等于内置默认即「默认」，被改过即「手动」。
    ///   来源只有一个：那个数本身。
    ///
    /// （若现场经验恰好也是 0.6，显示成「默认」无害 —— 数是同一个。）
    /// </summary>
    [Browsable(false)]
    public string WeldMinSource =>
        Math.Abs(WeldMinThicknessMm - WeldMinDefaultMm) < 1e-9
            ? "默认" : "手动（实际经验）";

[Category("5 C 整线 — 管几何"), DisplayName("焊接工艺最小厚度 [mm]"),
     Description("★★ **工艺下界的真实来源**（用户 2026-08-14）：\n" +
                 "「这个下限必须是工艺能够焊接铂金不变形的情况厚度」。\n\n" +
                 "此前程序用的 0.4 mm 来自「太薄没意义」的拍板（总纲·边界条件），\n" +
                 "那是个**任意数**；本参数把它换成有物理含义的量：\n" +
                 "凡是要**焊**的铂件（管纵缝、管↔法兰圆盘的环缝）都不得薄于它，\n" +
                 "否则焊接热输入会把薄板烧穿或翘曲，装不起来。\n\n" +
                 "⚠ **默认 0.6 mm 是待现场确认的假设**，不是实测：\n" +
                 "  取自铂制玻璃设备的常规做法（TIG 焊 0.5 mm 以下变形与烧穿风险显著上升）。\n" +
                 "  焊法不同差别很大（激光焊可更薄、电阻焊要求又不同）——\n" +
                 "  **这个数直接决定最终省铂结论**（§6 待补数据①），拿到实值请立刻覆盖。\n\n" +
                 "不受本下界约束的：舌片厚度（与圆盘同一张板料切出、不经焊缝时），\n" +
                 "以及任何纯电学/热学决定的局部加厚。")]
    public double WeldMinThicknessMm { get; set; } = 0.6;

    [Category("5 C 整线 — 管几何"), DisplayName("焊接下界安全系数"),
     Description("用户 2026-08-14：「这是理论计算，可以适当加入安全系数」。\n" +
                 "屈曲判据里有三个经验系数，不确定度并不小：\n" +
                 "  · C=0.2 收缩力系数 —— 在**钢**上标定的，铂上没有标定过\n" +
                 "  · β=2 焊道宽/板厚 —— 常规区间 1.5–3（±50 %）\n" +
                 "  · η_melt=0.35 熔化效率 —— 常规区间 0.3–0.5\n" +
                 "  · **k_b 板屈曲系数才是大头**：四边简支 4.0，一边自由仅 0.43，差 9.3 倍。\n" +
                 "    法兰盘正是「内边焊在管上、外边自由」⇒ 落在偏低那一侧。\n" +
                 "因为 k_b 已按最不利取，本系数只覆盖前三项 ⇒ 2.0 足够。")]
    public double WeldSafetyFactor { get; set; } = 2.0;

    [Category("3 A·B·C 共用 — 保温与表面"), DisplayName("端部额外保温厚度 mm"),
     Description("管**两端各一段**在基础保温之外再加的纤维厚度 mm（0 = 轴向均匀）。\n\n" +
                 "为什么需要它（用户 2026-08-14：「仅量把温度做均匀即可」）：\n" +
                 "冷坑只出现在每段两端各约 30 mm（= 热扩散长度 ℓt≈22 mm 的量级），\n" +
                 "中间 240 mm 是平的（±1.4 K）。补偿只需补在端部，不必动整段。\n\n" +
                 "机理：管子按同一电流均匀自发热，稳态下 q_joule = β·(T_set − T_amb)。\n" +
                 "端部局部加厚保温 ⇒ 该处 β 变小而发热不变 ⇒ **净剩余热量填坑**。\n" +
                 "⚠ 注意与「整体加厚保温」区分：整体加厚会让 ℓt 变长、坑反而更深\n" +
                 "  (ΔT = D/√(kAβ))。局部与整体方向相反，别混。\n\n" +
                 "为什么不用「管壁局部减薄」那条更直接的路：用户 2026-08-14 答\n" +
                 "「理论上可以，但就不能用拉管、只能用焊接，不建议没有好处」——\n" +
                 "等于在热区加一条通电的纵向焊缝，为填 17 K 的坑不值得。")]
    public double EndInsulExtraMm { get; set; } = 0.0;

    [Category("3 A·B·C 共用 — 保温与表面"), DisplayName("端部额外保温的长度 mm"),
     Description("自管两端各算起的长度 mm，在这一段内叠加「端部额外保温厚度」。\n" +
                 "取值参考：冷坑的衰减长度 ℓt ≈ 22 mm，实测剖面在 30 mm 处已恢复到 −0.5 K。")]
    public double EndInsulLengthMm { get; set; } = 30.0;

    [Category("3 A·B·C 共用 — 保温与表面"), DisplayName("安装姿态")]
    public Orientation Posture { get; set; } = Orientation.Horizontal;

    // ---------- 3 电气 ----------
    [Category("2 A·B·C 共用 — 电气"), DisplayName("许用电流密度 [A/mm²]"),
     Description("纯铂连续 8–10，短时极限 15。按 RMS 计")]
    public double JAllowAPerMm2 { get; set; } = 10.0;

    /// <summary>
    /// ★★★★★ **管子**的许用电流密度 A/mm²（与法兰分开）。
    ///
    /// 来源（用户 2026-08-15，现场）：
    ///   · **一般上限 15**
    ///   · **管壁 0.6 mm 时「不敢给到 15，12 应该是极限」**
    /// ⇒ 全档统一取 **12**：薄壁那档最紧，厚壁本来 J 就更低、不受影响；
    ///   这样不必发明一条随壁厚插值的规则。
    ///
    /// ⚠ 为什么要与 <see cref="JAllowAPerMm2"/> 分开：
    ///   后者同时被**法兰**用（自动定厚、J_max 判据），而法兰 J 实测约 34，
    ///   且 §4.2j 已证「高 J 不等于局部过热」—— 用户给的 15/12 只针对管子。
    ///   混用会把一个有据的数按到一个无据的地方去。
    ///
    /// ★ 这个数原先是 10（占位值，注释自明「物理依据待定」§4.2i），却同时
    ///   在稳态判据里当**参考量**、在 <c>RampSolver</c> 里当**硬上限** ——
    ///   同一个无据的数两处待遇相反，是它把管壁 0.6 判成不可行的
    ///   （升温电流上限 954 A &lt; 稳态所需 1045 A ⇒ 永远到不了控温点）。
    ///   现在有来源了 ⇒ **管 J 判据同步从「参考」升为「硬判据」。**
    /// </summary>
    [Category("2 A·B·C 共用 — 电气"), DisplayName("管许用电流密度 [A/mm²]"),
     Description("用户 2026-08-15 现场：一般上限 15；管壁 0.6 时 12 是极限。全档取 12")]
    public double TubeJAllowAPerMm2 { get; set; } = 12.0;

    [Category("2 A·B·C 共用 — 电气"), DisplayName("二次电源型式")]
    public SupplyMode Supply { get; set; } = SupplyMode.AcPhase;

    [Category("2 A·B·C 共用 — 电气"), DisplayName("直流偏置 [% of I_rms]"),
     Description("交流下应为 0。反并联可控矽触发角不对称会产生直流分量，" +
                 "该分量驱动玻璃电解 → 铂阳极溶解 + 碱迁移。用钳表直流档实测填入")]
    public double DcOffsetPercent { get; set; } = 0.0;

    [Category("2 A·B·C 共用 — 电气"), DisplayName("玻璃电阻率 [Ω·cm]"),
     Description("工作温度下的熔体电阻率。含碱玻璃 1–10，硼硅/无碱 50–500。仅用于直流分量核算")]
    public double GlassResistivityOhmCm { get; set; } = 5.0;

    [Category("2 A·B·C 共用 — 电气"), DisplayName("铂溶解价态 z"),
     Description("法拉第定律用。Pt²⁺ 取 2，Pt⁴⁺ 取 4（保守取 2）")]
    public double PtValence { get; set; } = 2.0;

    // ---------- 4 铂表面 / 保温 ----------
    [Category("3 A·B·C 共用 — 保温与表面"), DisplayName("铂表面发射率 ε"),
     Description("抛光 0.10–0.15，使用后发暗 0.20–0.30。裸管时热损失与本值成正比")]
    public double PtEmissivity { get; set; } = 0.18;

    [Category("3 A·B·C 共用 — 保温与表面"), DisplayName("保温外表面发射率 ε_out"),
     Description("氧化铝纤维/致密氧化铝约 0.4–0.6")]
    public double OuterEmissivity { get; set; } = 0.45;

    // ★ HANDOVER §4.2l：用户「稳态电流密度远低于 10 A/mm²」，而模型算出管 J≈10.3 顶在限值上；
    //   同一根因还造成玻璃温降 模型 41 K vs 实测 20 K。散热高估 ⇒ 功率大 ⇒ 电流大 ⇒ J 大、抽热大。
    //   本系数是**集总标定量**，不预设高估来自哪一环（发射率？纤维实际更厚？环境不是 25 °C
    //   而是被炉膛包围？氧化铝托管接触？）—— 那些只有现场才能分辨。
    //   实现上对「层导热 + 表面换热」**同倍**缩放，于是各界面温度不变而热流严格 ×LossScale
    //   （只缩表面在纤维热阻主导时几乎无效；只缩最终结果则内外能量不闭合）。
    //   标定方法见 CLI --calib。默认 1.0 = 不标定，与历史结果一致。
    [Category("3 A·B·C 共用 — 保温与表面"), DisplayName("散热标定系数"),
     Description("整条散热通道的集总标定倍率。1.0 = 模型原值；0.5 = 实际散热只有模型的一半。\n" +
                 "由 --calib 用实测段电流或实测玻璃温降反标定。见 HANDOVER §4.2l")]
    public double LossScale { get; set; } = 1.0;

    // ★ 2026-08-10 按现场实况修正：纤维包覆厚度 **2–3 mm**（原设 10 mm，差 4 倍）。
    //   保温热阻几乎全部由纤维贡献（致密氧化铝 k≈9 W/m·K，其 5 mm 只占总热阻约 1 %），
    //   故这一项直接决定散热量级 —— 改动会连带影响电功率、法兰自给率 Φ 与升温核算。
    [Category("8 ✗ 被页面/设计记录接管（改了对整线链没用）"), DisplayName("① 内层（贴铂）"),
     Description("⚠ 本项被「③ 整线核算」页的「纤维保温 mm」控件接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　现场实测包覆厚度 2–3 mm，取中值 2.5。热阻几乎全在这一层")]
    public InsulationLayer Layer1 { get; set; } = new()
    { Name = "高纯氧化铝纤维", ThicknessMm = 2.5, K0 = 0.04, K1 = 3.0e-4 };

    // ⚠ 实物是**半管套**（仅下半圈，见 Pt_Heater.3dm 图层「氧化铝管」，内 R26/外 R31，Z≤0）。
    //   此处仍按整圈同心层处理 —— 因其热阻只占约 1 %，影响主要在外表面半径与发射率，
    //   量级上可接受；若要精确需改为按包角加权的并联热阻。
    [Category("3 A·B·C 共用 — 保温与表面"), DisplayName("② 中层"),
     Description("致密氧化铝半管套（实物仅下半圈）。热阻占比 ~1%，此处按整圈近似")]
    public InsulationLayer Layer2 { get; set; } = new()
    { Name = "致密氧化铝半管套", ThicknessMm = 5, K0 = 25.0, K1 = -0.016 };

    [Category("3 A·B·C 共用 — 保温与表面"), DisplayName("③ 外层（可选）")]
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

    [Category("8 ✗ 被页面/设计记录接管（改了对整线链没用）"), DisplayName("法兰有保温"),
     Description("⚠ 本项被「③ 整线核算」页的「法兰保温」下拉接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　法兰双面包覆高纯氧化铝纤维。降低 q″ 会降低自给所需的厚度")]
    public bool FlangeInsulated { get; set; } = true;

    // ★ 2026-08-10 随 Layer1 一并改为 2.5：用户给的「纤维包覆 2–3 mm」是针对铂管的，
    //   此处按「同一材料同一工艺」外推到法兰。**待现场确认**（见 HANDOVER §6 待补数据 ⑦）。
    //   留 10 mm 而管子改 2.5 mm 会物理不自洽：管子保温薄 ⇒ 电流大 ⇒ 同一电流流过裹得厚的法兰
    //   ⇒ 法兰过热、Φ≫1 ⇒ 向管根倒灌，实测算出管根 2137 °C（超铂熔点 1768 °C）。
    [Category("8 ✗ 被页面/设计记录接管（改了对整线链没用）"), DisplayName("法兰保温厚 [mm]"),
     Description("⚠ 本项被「③ 整线核算」页的「法兰保温厚 mm」控件接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　单面厚度，材料取内层①的 k(T)。按与铂管同一包覆工艺取 2.5，待现场确认")]
    public double FlangeInsulThickMm { get; set; } = 2.5;

    [Category("6 C 整线 — 法兰边界"), DisplayName("法兰吹风风速 [m/s]"),
     Description("压缩空气强制冷却。0 = 仅自然对流。每吹掉一瓦都要由铂金发出来，直接折算成铂重")]
    public double FlangeAirVelocityMPerS { get; set; } = 0.0;

    [Category("6 C 整线 — 法兰边界"), DisplayName("对流特征长度 [m]"),
     Description("★★ 承重且**没有依据**的一个数 —— 2026-08-28 查出并实测（见 HANDOVER §0.0.3 ⑱）。　" +
                 "它是 Churchill–Chu 自然对流与平板强制对流**唯一的几何输入**。　" +
                 "此前写死 0.05 且**四处各存一份**（ShellThermal、DesignScreen×2、RampTwoNode），" +
                 "全都与几何脱钩 —— 而盘径与舌长正是被优化的变量。　" +
                 "⚠ 实测灵敏度**极高**：把它换成网格包围盒跨度（约 0.17 m），" +
                 "设计记录 0.8 档的 ②′ 从 +1.123 W 翻成 **−1.880 W**（负 = 热往管里灌，烧断方向），" +
                 "③ 从 +5.182 K 翻成 −0.643 K，两个设计记录双双「自己不过判据」。　" +
                 "⚠ 但 0.17 同样是**猜的**：Churchill–Chu 要的是**竖直板高度**，" +
                 "而这片板在现场怎么摆（舌片朝下 ⇒ ~170 mm；盘立舌横 ⇒ ~60 mm）**没有确认过**。　" +
                 "⇒ 保留 0.05 只是**保持现状**，不是有依据。**必须现场确认安装姿态与特征高度。**")]
    public double ConvCharLenM { get; set; } = 0.05;

    [Category("8 ✗ 被页面/设计记录接管（改了对整线链没用）"), DisplayName("铜排夹持温度 [°C]"),
     Description("⚠ 本项被「③ 整线核算」页的「铜排夹持 °C」控件接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　舌片末端整条边的强制温度。<0 = 无夹冷（自由辐射端）。" +
                 "空冷即可，不需要水冷 —— 400 °C 与 80 °C 的差别仅约 10 W。" +
                 "★ 这一项是现场把法兰自给率整定到位的**唯一可调旋钮**（HANDOVER §4.2v）")]
    public double BusbarClampTempC { get; set; } = -1;

    [Category("8 ✗ 被页面/设计记录接管（改了对整线链没用）"), DisplayName("铜排压接长度 [mm]"),
     Description("⚠ 本项被**设计记录**接管：整线链的两条路都强制取 DesignSpec.Current.ClampLengthMm（现为 40 mm，见 DesignSpec.cs 与 LineDesignPage.PageToDesignSpec）—— 在这张表里改它，对「③ 整线核算」没有影响。本表显示的 3 mm 只对「② 粗算」的单段解有效。　沿舌片方向的压接长度，即定温边界的深度。\n" +
                 "★ 早先在网格里硬编码为 3 mm —— 那是**数值边界，不是设计值**：\n" +
                 "  3 mm × 舌宽 40 mm = 120 mm² 接触面，共用片 1099 A ⇒ 界面电流密度约 9 A/mm²，\n" +
                 "  而铜排压接通常按 ≤1 A/mm² 量级设计，差一个数量级 —— 现场做不出来。\n" +
                 "加长它会把定温边界推向圆盘、缩短导热路径 ⇒ 铜排带走的热增加，**必须重算**。")]
    public double BusbarClampLengthMm { get; set; } = 3.0;

    [Category("6 C 整线 — 法兰边界"), DisplayName("铜排总热导 [W/K]"),
     Description("★ 铜排到冷端的总热导 G = k_Cu·A/L（含铜排自身表面散热）。\n" +
                 "≥0 时**取代** BusbarClampTempC 的二选一，改用第三边界：q = G·(T_舌端 − T_冷端)。\n\n" +
                 "为什么必须有它：此前舌端只有两种边界，而**两种都不是真的**——\n" +
                 "  · 定温：假设铜排无论要带走多少热都能把接触点按在 300 °C，等于假设结论；\n" +
                 "  · 自由端：假设铜排完全不导热。实算给出舌端 1100–2400 °C，\n" +
                 "    而**铜的熔点只有 1085 °C** ⇒ 该工况下接头根本不存在。\n" +
                 "  §4.3c/§4.3d 的「端片自由端」结论正是建立在后者上。\n\n" +
                 "典型值：40×21.8 mm² 铜排、到冷端 300 mm ⇒ G = 385×873e-6/0.3 ≈ 1.1 W/K。\n" +
                 "把 G 当设计变量，接头温度就从**假设**变成**输出**，可以拿去对铜的许用温度。")]
    public double BusbarConductanceWPerK { get; set; } = -1;

    [Category("6 C 整线 — 法兰边界"), DisplayName("铜排许用电流密度 [A/mm²]"),
     Description("★ 铜排截面的第一性原理来源：A = I / J许用。自然对流 1.5–2，强制风冷 3–4。　" +
                 "2.0 与 `--cli --busbar` 选型表用的是同一个数（此前那张表把它写死在调用里）。　" +
                 "⚠ 它与「铜排到冷端长度」一起决定**逐片**热导 G = k_Cu·A/L —— " +
                 "四片电流不同（实测 685/1099/975/542 A），G 本来就该逐片不同。")]
    public double BusbarJAllowAPerMm2 { get; set; } = 2.0;

    [Category("6 C 整线 — 法兰边界"), DisplayName("铜排到冷端长度 [mm]"),
     Description("★ 从压接点到冷端（散热器/环境）的铜排长度。与许用电流密度一起定 G = k_Cu·A/L。　" +
                 "300 与 `--cli --busbar` 选型表同源。**待现场确认**：实际走线长度决定它。")]
    public double BusbarLenToSinkMm { get; set; } = 300.0;

    [Category("6 C 整线 — 法兰边界"), DisplayName("铜排冷端温度 [°C]")]
    public double BusbarSinkTempC { get; set; } = 25;
    [Category("6 C 整线 — 法兰边界"), DisplayName("共用片抽热两侧均分"),
     Description("★ 能量守恒开关（2026-08-28）。　" +
                 "QFromTubeW 是一片法兰经整圈管孔抽走的总量；而外层耦合把每片的全额" +
                 "挂到相邻段端 ⇒ 内部共用片被两段各扣一次：" +
                 "管子失去 Q0+2×ΣQ内+Qn，法兰只收到 ΣQ。　" +
                 "实测残差（--selfcheck A 段「热收支」）：0.8 档 +2.66 W、0.6 档 +3.74 W，四个档全非零。　" +
                 "打开后：端片（只属于一段）拿整份，内部共用片各半 ⇒ Q_L + Q_R = Q，严格守恒。　" +
                 "⚠ 默认关：打开会改动设计记录的数（两档要重解、五个回归基准要重填）；" +
                 "先跑对照、看清影响方向，再谈要不要重新跑一次。　" +
                 "⚠ 各半是领头阶正确解（两侧是同一根管、同样的导热）；" +
                 "更精细的做法是按两侧管端各自的导热通量加权 —— 那要动求解器，尚未做。")]
    public bool SplitSharedFlangeDraw { get; set; } = false;

    [Category("6 C 整线 — 法兰边界"), DisplayName("基线收敛判据与主环同口径"),
     Description("★ 第一性原理开关（2026-08-28 普查查出）。　" +
                 "③（法兰增量温降）= 无法兰基线 − 实际。主环判收敛用的是「到不动点的距离」" +
                 "（真残差 × 放大 25，放大 = 1/(1−g)，实测环路增益 g≈0.96），" +
                 "而**基线环路拿欠松弛步直接比容差** —— 两个错叠在一起：" +
                 "① 参照系是欠松弛步（= ω×残差，ω=0.35），主环自己的注释写着「参照系是残差、不是欠松弛步」；" +
                 "② 没乘不动点放大。　" +
                 "⇒ 基线停在 δ<1.0 K ⇒ 真残差≈2.9 K ⇒ 到不动点≈71 K，而 ③ 的限值是 10 K。　" +
                 "⚠ 这个失效模式代码自己写在那几行上面：「基线悄悄地不收敛，③ 会整体偏掉几十 K " +
                 "而判据表照样打得漂漂亮亮」——他们修了裸 Picard 与轮数，却没修判据本身。　" +
                 "★ 2026-08-28 **已翻为默认开**，依据是配对实测（同代码、同网格、只差本开关）：　" +
                 "细网格 0.408 mm 上 ③ 由 10.539（**不过**）变成 9.572（**过**），" +
                 "0.817 mm 档同步由 10.095 变 9.129 —— 两档偏移一致（−0.966/−0.967），" +
                 "而 ②′/②″/铂重**逐位不变**。　" +
                 "⇒ 「历史 0.8 档在算得准的网格上不合格」这个结论是**判据 bug**，不是物理。　" +
                 "关掉它（--basetol-legacy）只用于复现历史数字，不得用于交付。")]
    public bool BaselineTolAmplified { get; set; } = true;

    [Category("6 C 整线 — 法兰边界"), DisplayName("电位场用旧的 Gauss–Seidel"),
     Description("★ 只为**配对对照**（2026-08-29）。　" +
                 "电位场已改用 **CG + Jacobi 预条件**：旧的 Gauss–Seidel 在泊松型问题上" +
                 "迭代次数 ~ 条件数 ~ h⁻² ~ n ⇒ 总成本 ~ n²；实测单元 6.4× 而每轮耗时 96×。　" +
                 "而且旧解的**收敛判据拿的是「步长」不是残差** —— 与基线耦合环、" +
                 "CoupledSolver 那两处同一个错，慢收敛时步长很小而残差很大。　" +
                 "⚠ 打开只用于搞清楚「哪一改动把 ③ 改了」，**不得用于交付**。")]
    public bool LinearGaussSeidel { get; set; } = false;

    [Category("6 C 整线 — 法兰边界"), DisplayName("电流场按 σ(T) 重解"),
     Description("★ 第一性原理开关（2026-08-28）。　" +
                 "ShellCurrent.Solve 的 tempC 形参就是为 σ(T) 造的，机制写对了，" +
                 "但全仓 12 个调用点没有一个传过它 ⇒ 交付用的整线链恒按等温解电流场。　" +
                 "同一片法兰上管孔 1150 °C、压接段 450 °C、舌片可超 1400 °C，" +
                 "ρe(450)/ρe(1150) ≈ 0.45 ⇒ 冷区更导电、电流往那头挤，等温模型看不见 —— " +
                 "而 ②″ 判的正是局部电流拥塞造成的峰值。　" +
                 "★ 项目做对过：已被取代的 CoupledSolver 每轮都用新温度场重解电流场" +
                 "（注释：铂 700–1300 °C 间 ρe 变化 48 %），重写时丢掉了。　" +
                 "⚠ 默认关：打开会改判据的数（②″ 与局部热稳定首当其冲）。")]
    public bool SigmaOfTCoupling { get; set; } = false;

    // ---------- 6 玻璃物性 ----------
    [Category("4 A·B·C 共用 — 玻璃物性"), DisplayName("密度 [kg/m³]")]
    public double GlassDensity { get; set; } = 2500;

    [Category("4 A·B·C 共用 — 玻璃物性"), DisplayName("比热 [J/kg·K]")]
    public double GlassCp { get; set; } = 1300;

    [Category("4 A·B·C 共用 — 玻璃物性"), DisplayName("内壁换热系数 hg [W/m²K]"),
     Description("由层流 Nu≈3.66 与玻璃熔体导热 k≈0.9 W/m·K 得 hg = Nu·k/D ≈ 65（ID50）。\n" +
                 "旧默认值 220 对应 k_eff≈2.2，远高于熔体导热，会把玻璃向管壁的放热放大约 3.4 倍，\n" +
                 "使 --glass 的全程温降算成 67.6 K 而实测仅 20 K。改动依据见 --glass 的反解。")]
    public double HGlass { get; set; } = 65;

    [Category("4 A·B·C 共用 — 玻璃物性"), DisplayName("粘度 [Pa·s]"), Description("压降估算用")]
    public double GlassViscosity { get; set; } = 30;

    [Category("6 C 整线 — 法兰边界"), DisplayName("法兰抽热覆盖 [W]"),
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

    /// <summary>
    /// 管**左/右端各自**的法兰抽热 W。NaN = 两端都用 <see cref="FlangeDrawOverrideW"/>。
    ///
    /// ⚠ 2026-08-14 修的一个 bug：`LineRunner` 原本写
    /// `target[i] = 0.5*(a+b)` —— 把一段两端**两片不同法兰**的抽热取平均后挂到两端，
    /// 注释还写着「SegmentSolver 两端挂同一个值，取均值」。
    /// 但 `Bvp1D.Solve` 本来就收独立的 bcL/bcR，这个平均纯属没必要。
    ///
    /// 实测影响：入口片抽 +5 W、HC1|HC2 抽 +2 W，被抹成两端各 3.5 W ——
    /// 而管根温差 C2 与判据 ② 正是由这个量定的，两端不对称时会一头偏冷一头偏热。
    /// </summary>
    [Browsable(false)] public double FlangeDrawLeftW { get; set; } = double.NaN;
    [Browsable(false)] public double FlangeDrawRightW { get; set; } = double.NaN;

    /// <summary>
    /// **相邻段端部温度** °C（NaN = 该端无邻段，如整线的进出口）。
    ///
    /// ⚠⚠ 2026-08-14 发现的结构性缺陷：每段管**各解各的**，两端只挂「法兰抽热」，
    /// **段与段之间的轴向导热根本没接上**。实测同一物理位置 HC1 侧 1141.6 °C、
    /// HC2 侧 1072.5 °C —— **断层 69 K**，而管子是连续的铂管，这不可能。
    ///
    /// 漏掉的热流量级：q = kAΔT/ℓ ≈ 72×9.5e-5×69/0.022 ≈ **21 W**，
    /// 而法兰抽热 D 实测只有 2–9 W —— **漏掉的比要算的还大**。
    /// 而 C2、冷点深度、判据 ② 全部由 D 决定。
    ///
    /// 修法：端部通量加一项 G·(T − T_邻)，G = kA/Δx（一个节距的导度 = 连续性极限）。
    /// `Bvp1D.Boundary.WithFlux` 收的正是端温的函数，故不需要重构求解器。
    /// </summary>
    [Browsable(false)] public double NeighbourTempLeftC { get; set; } = double.NaN;
    [Browsable(false)] public double NeighbourTempRightC { get; set; } = double.NaN;

    [Category("9 ✗ 对整线链无效"), DisplayName("壁厚由程序反算"),
     Description("⚠ 本项被LineRunner 强制置 false —— 整线链壁厚由 LineCase.WallMm 定，自行反算会与之打架接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　关闭 = 校核模式：壁厚取「最小可制造壁厚」的实测值，程序只报实际 J")]
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
    [Browsable(false)] public double TubeJAllow => TubeJAllowAPerMm2 * 1e6;   // A/m²
    [Browsable(false)] public double GlassRho => GlassResistivityOhmCm * 1e-2;  // Ω·m
    [Browsable(false)] public double MassFlow => ThroughputTPerDay * 1000.0 / 86400.0; // kg/s

    [Browsable(false)]
    public List<InsulationLayer> Layers => new() { Layer1, Layer2, Layer3 };
}
