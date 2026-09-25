using System.ComponentModel;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★ 审排版（2026-09-10）：参数表里枚举按 [Description] 的中文显示（原来漏出 AcPhase／Horizontal 这类英文代号），
/// 反向也认中文与原名。
/// </summary>
public sealed class DescribedEnumConverter : EnumConverter
{
    public DescribedEnumConverter(System.Type t) : base(t) { }
    private static string Name(object v)
    {
        var f = v.GetType().GetField(v.ToString() ?? "");
        var d = f?.GetCustomAttributes(typeof(DescriptionAttribute), false).OfType<DescriptionAttribute>().FirstOrDefault();
        return d?.Description ?? v.ToString() ?? "";
    }
    public override object? ConvertTo(ITypeDescriptorContext? c, System.Globalization.CultureInfo? ci, object? v, System.Type dest)
        => dest == typeof(string) && v is not null ? Name(v) : base.ConvertTo(c, ci, v, dest);
    public override object? ConvertFrom(ITypeDescriptorContext? c, System.Globalization.CultureInfo? ci, object v)
    {
        if (v is string s)
        {
            foreach (var e in System.Enum.GetValues(EnumType)) if (Name(e) == s || e.ToString() == s) return e;
        }
        return base.ConvertFrom(c, ci, v);
    }
}

/// <summary>审排版（2026-09-10）：布尔在参数表里显示 是／否（原来是 True／False）。</summary>
public sealed class ChineseBoolConverter : BooleanConverter
{
    public override object? ConvertTo(ITypeDescriptorContext? c, System.Globalization.CultureInfo? ci, object? v, System.Type dest)
        => dest == typeof(string) && v is bool b ? (b ? "是" : "否") : base.ConvertTo(c, ci, v, dest);
    public override object? ConvertFrom(ITypeDescriptorContext? c, System.Globalization.CultureInfo? ci, object v)
        => v is string s ? (s.Trim() is "是" or "true" or "True") : base.ConvertFrom(c, ci, v);
    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? c) => new(new object[] { true, false });
}

/// <summary>审排版（2026-09-10）：「−1 = 自动」这类哨兵值在参数表里显示成「自动」，不再漏出 −1。</summary>
public sealed class AutoOrValueConverter : DoubleConverter
{
    public override object? ConvertTo(ITypeDescriptorContext? c, System.Globalization.CultureInfo? ci, object? v, System.Type dest)
        => dest == typeof(string) && v is double d && d < 0 ? "自动（程序自己算）" : base.ConvertTo(c, ci, v, dest);
    public override object? ConvertFrom(ITypeDescriptorContext? c, System.Globalization.CultureInfo? ci, object v)
    {
        if (v is string s)
        {
            string t = s.Trim();
            if (t.Length == 0 || t.StartsWith("自动") || t == "-1" || t == "−1") return -1.0;
            if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        }
        return base.ConvertFrom(c, ci, v);
    }
}

[TypeConverter(typeof(DescribedEnumConverter))]
public enum SupplyMode
{
    [Description("单相交流（可控矽相控）")] AcPhase,
    [Description("单相交流（过零/周波）")] AcZeroCross,
    [Description("直流（整流）")] Dc
}

[TypeConverter(typeof(DescribedEnumConverter))]
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
    // ---------- 0 判据限值与可行窗口 ----------
    //
    // ★★★★★ U 路（2026-09-18，Opus 5）：**温差预算改成工程师可填。**
    //
    //   用户 2026-09-17：「简单说：按半毫米一层缠，现在的判据下没有能造的设计……
    //   要你定一件事：冷侧那 5 度能不能放宽」；2026-09-18：「OK!了解了，请开工」。
    //   ⇒ 冷侧、热侧两条判据的限值从写死的 5 K 改成这两项输入，默认仍是 5 K。
    //   **我不替填的人改这个数**：默认值一位不动，放宽多少由填的人负责，界面上写明出处。
    //
    //   限值只有一条路：这两项 → LineCase.ColdUnderTcMaxK／HotOverTcMaxK（整线算例读 Base）
    //   → 判据表、保温搜索、加密复算容差、说明书。别处不许再写第二个数（门：R48UTempBudgetTests）。

    [Category(ParamCat.判据限值与窗口), DisplayName("管根低于热偶读数 允许值 [K]"),
     Description("法兰所在接头处管根较冷的那一端，允许比控温热偶的读数低多少 —— 法兰抽热会把管根拉冷。"
               + "默认 5 = 控温热偶在 1100 °C 的误差（2026-09-14 现场给的：热偶装在每段中点，误差 5 ℃，「10 ℃ 是上下各 5 ℃」）。"
               + "只在带玻璃稳态卡交付；空管到温稳态照常算、只作参考。")]
    public double ColdUnderTcAllowK { get; set; } = LineCase.ThermocoupleErrorK;

    [Category(ParamCat.判据限值与窗口), DisplayName("最热铂高出热偶读数 允许值 [K]"),
     Description("法兰上（圆盘和舌片）以及它贴着的管根里，最热的那一点允许比控温热偶的读数高多少。"
               + "默认 5 = 控温热偶在 1100 °C 的误差（出处同上一项）。"
               + "只在带玻璃稳态卡交付；空管到温稳态照常算、只作参考。")]
    public double HotOverTcAllowK { get; set; } = LineCase.ThermocoupleErrorK;

    // ★★★★★ 决 103（业主 2026-09-24）：稳态期（带玻璃）第 j 片的热侧判据换成「法兰最热处高出管接触处温度」，限值 10 K（出处 CriteriaRules.HotOverContactMaxKDefault）。
    //   照 U 路两项的写法：参数表一项 → LineCase.HotOverContactMaxK（哨兵 NaN = 跟着参数表走）→ 判据表、求解器。别处不许再写第二个 10。
    [Category(ParamCat.判据限值与窗口), DisplayName("法兰最热处高出管接触处温度 允许值 [K]"),
     Description("带玻璃稳态：每片法兰温度场的最高温允许比该片管接触处温度（模型算的管根接触温度）高多少。"
               + "默认 10（2026-09-24 定：法兰比管接触处略热的方向是对的，可容许到 10 °C 以内；拉低管温的方向不许 —— 那一侧由「管接触处流入法兰的净热流 ≤ 0」卡，不给预算）。"
               + "只在带玻璃稳态卡交付；空管到温稳态照常算、只作参考。")]
    public double HotOverContactAllowK { get; set; } = CriteriaRules.HotOverContactMaxKDefault;

    [Category(ParamCat.判据限值与窗口), DisplayName("终验时量每片舌保温的可行窗口"),
     TypeConverter(typeof(ChineseBoolConverter)),
     Description("终验（加密复算到数不再变）之后，逐片把舌保温上下各挪一点、其余一位不动，量出「这一片还能在多宽的范围里改仍然全过」。"
               + "结果进判据页的小表与安装报告：窗口里落不进任何一档缠绕层数（现场一层 0.5 mm）的片会被点名。"
               + "很慢（每片二十几次整线解，细网格上合计约一小时），只在终验跑一次，优化过程中不跑。不想等就关掉。")]
    public bool MeasureInsulWindowAtFinalCheck { get; set; } = true;

    // ★★★★★ 2026-09-18，Opus 5：**三关按顺序跑，结论一起给。**
    //
    //   用户口径（2026-09-15/16）：升温全程先过 → 带玻璃稳态决定法兰设计成不成 → 空管到温只卡电流密度与场有效 → 再看铂重。
    //   在此之前 APP 只跑了中间那一关：升温全程（RampSweep）**全仓没有生产调用方**（只有测试在调），
    //   空管到温稳态也只有命令行与测试跑过 —— 工程师在界面上点不到其中两关，
    //   而判定却按「三关都过」在说话。**算得出、点不到**，本项目最常栽的那一族。
    //   ⇒ 终验（加密复算到数不再变）之后按顺序跑齐三关，结论按顺序印全名。

    [Category(ParamCat.判据限值与窗口), DisplayName(FinalCheckReport.SwitchLabel),
     TypeConverter(typeof(ChineseBoolConverter)),
     Description("终验（加密复算到数不再变）之后，在**同一张判决网格**上按顺序跑："
               + "先升温全程（逐设定点解一次整线：场有效、管与截面的电流密度按该点实际电流都不超限；伸长只报数不卡），"
               + "再带玻璃稳态（就是刚复算完的那一份，不重跑），再空管到温稳态，最后给铂重。"
               + "结论与每段管、每片法兰的伸长量进输出框与安装报告。"
               + "慢（升温全程八个设定点各一次整线解，加上空管一次）。关掉就只剩带玻璃稳态那一关，"
               + "界面与报告照实写「没跑」——没跑不等于过。")]
    public bool RunThreeStatesAtFinalCheck { get; set; } = true;

    // ---------- 1 工艺 ----------
    [Category(ParamCat.页面接管), DisplayName("目标金属温度 [°C]"),
     Description("⚠ 本项被「③ 整线核算」页的分段控温点表接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　管中段控温点的金属温度设定值")]
    public double TSetC { get; set; } = 1300;

    [Category(ParamCat.工艺条件), DisplayName("玻璃液相线 T_liq [°C]"),
     Description("析晶判据基准。全程金属温度必须高于 T_liq + 裕度")]
    public double TLiquidusC { get; set; } = 1050;

    [Category(ParamCat.工艺条件), DisplayName("析晶温度裕度 [K]")]
    public double DevitMarginK { get; set; } = 40;

    [Category(ParamCat.工艺条件), DisplayName("产量 [t/day]")]
    public double ThroughputTPerDay { get; set; } = 1.5;   // 现场实测值

    [Category(ParamCat.工艺条件), DisplayName("玻璃进口温度 [°C]")]
    public double TGlassInC { get; set; } = 1300;

    [Category(ParamCat.页面接管), DisplayName("本段玻璃压力水头 [m]"),
     Description("⚠ 本项被「③ 整线核算」页的分段水头接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　铂管为分段控制，每段的液柱高度不同 —— 按本段实际值填。" +
                 "决定管内压，进而决定环向应力 σθ = p·r/t")]
    public double GlassHeadM { get; set; } = 0.5;

    [Category(ParamCat.工艺条件), DisplayName("支承跨距 [mm]"),
     Description("铂管两支承点间距。默认取段长（两端法兰即支承点）")]
    public double SupportSpanMm { get; set; } = 300;

    /// <summary>
    /// ⚠ <see cref="GradeNameConverter"/> 把它做成**只能选的下拉** —— 见那里的说明：
    ///   自由文本时打错一个字，整线解会跑到最后才在 MaterialDb.Get 上崩。
    /// </summary>
    [Category(ParamCat.管几何), DisplayName("铂材牌号"),
     TypeConverter(typeof(GradeNameConverter)),
     Editor(typeof(PtOptimize.UI.GradeNameEditor), typeof(System.Drawing.Design.UITypeEditor)),
     Description(GradeNameNote)]
    public string GradeName { get; set; } = GradeChoices.DefaultGrade;

    /// <summary>
    /// ★★★★★ 参数表「铂材牌号」的说明 —— **说明里写的必须等于求解链真读的**（门 <c>R48MGradeNoteTruthTests</c> 两头钉）。
    ///
    /// 沿革：2026-09-18（Opus 5）以前说明写「电阻率与持久强度均取该牌号的实测数据」，而求解链的电阻率、热导率、比热
    /// 读的都是 <see cref="Materials"/> 写死纯铂的那几支 —— 假话；当日改成「电阻率、热导率、比热目前一律按纯铂算」的真话，只改说明、不改求解链。
    ///
    /// **2026-09-23（Opus 5.5，R48 物性接线）起的现状**：
    ///   · 电阻率（含 dρ/dT 与电阻温度系数）、热导率、比热一律经 <see cref="PtProps"/> 按本牌号取 ——
    ///     段解、壳体电流与温度场、板件二维电流与温度场、升温两节点与准静态、升温集总、热稳定（局部与整片）、设计电流闭式电阻都读它；
    ///     牌号为纯铂时 <see cref="PtProps"/> 调的就是 <see cref="Materials"/> 原函数，纯铂的数逐位不变（门 <c>R48PropsWiringGateTests</c>）。
    ///   · 读进来的牌号若电阻率或热导率／比热不算「自有」（<see cref="MaterialDb.DataCompleteness"/>），这几项**一起**退回纯铂、不混用，结果说明里写明缺什么。
    ///   · 仍按纯铂的：**密度**（<see cref="Materials.PtDensity"/>；热容 = 纯铂密度 × 所选牌号比热，铂重与自重同样按纯铂密度 —— 材料库里的合金密度没有出处）、
    ///     **熔点**与电阻率拟合区护栏（纯铂值）。
    ///     **焊缝屈曲下界**也仍按纯铂：<see cref="DesignSpec.DiscFloorMm"/> → <see cref="WeldDistortion.ForPt"/> 用的是
    ///     线胀系数 <see cref="Materials.PtAlphaExp"/>、到熔点的平均比热 <see cref="Materials.PtCpMeanToMelt"/>、熔化潜热 <see cref="Materials.PtLatentFusion"/>、
    ///     泊松比 <see cref="Materials.PtPoisson"/>（与熔点一起都是纯铂常数，不经 <see cref="PtProps"/>）。
    ///     持久强度原来就按牌号；热膨胀按牌号只指升温扫描的热应变那一路（<see cref="FinalCheck"/> 把参数表牌号交给 <see cref="RampSweep"/>，它按牌号读热膨胀曲线表），焊缝屈曲下界那一处不算在内。
    ///   · 与纯铂的差量见 deliverable 里「物性按牌号与纯铂差量」那份带开跑时刻的文件。Pt-Rh/90-10 在差量文件所列温度（20 °C 起）中
    ///     最多是电阻率 +84.91 %、热导率 −40.82 %，都在 20 °C。热导率那一格是端点值：20 °C 低于该牌号热导率数据点下限 100 °C，取的是端点 42.5 W/(m·K)，不是 20 °C 的数据。
    /// 谁把哪一支改回直读纯铂而没回来改这句话，门 <c>R48MGradeNoteTruthTests</c> 与 <c>R48PropsWiringGateTests</c> 的源码门当场红。
    /// </summary>
    public const string GradeNameNote =
        "材料库中的牌号名（**只能从下拉里选**，打不了字）。" +
        "四类数据（电阻率／热膨胀／持久强度／热导率与比热）不全的牌号在下拉里**灰显、不能选**，行末写明缺哪几类。" +
        "默认纯铂。" +
        "**持久强度按牌号**；**电阻率（含电阻温度系数）、热导率、比热也按所选牌号**（段解、法兰电流与温度场、升温、热稳定都读它）。" +
        "仍按纯铂的：**密度**（热容＝纯铂密度×所选牌号比热；整线核算的铂重与自重同样按纯铂密度 —— 材料库里合金密度没有出处）、**熔点**（纯铂值）与电阻率拟合区护栏。" +
        "例外：焊缝屈曲下界（圆盘最薄厚度的估算）仍按纯铂的线胀系数、到熔点的平均比热、熔化潜热与泊松比。" +
        "读进来的牌号若电阻率或热导率／比热数据不全，这几项一起按纯铂算，并在结果说明里写明。" +
        "与纯铂的差量见 deliverable 里「物性按牌号与纯铂差量」那份带开跑时刻的文件。";

    // ★★ 2026-09-18，Opus 5：这两项的**出处**写进说明 —— 说明只有一份写法（TubeStrength 的两个常量），
    //   参数表、报告、判据说明、HANDOVER 都引它，不许各抄一句。
    [Category(ParamCat.工艺条件), DisplayName("设计寿命 [h]"),
     Description("1 年 = 8760 h。" + TubeStrength.LifeNote)]
    public double DesignLifeHours { get; set; } = 8760;

    [Category(ParamCat.工艺条件), DisplayName("力学安全系数"),
     Description(TubeStrength.SafetyFactorNote)]
    public double SafetyFactor { get; set; } = 2.0;

    [Category(ParamCat.保温与表面), DisplayName("环境温度 [°C]")]
    public double TAmbC { get; set; } = 25;

    // ---------- 2 供料管几何 ----------
    [Category(ParamCat.管几何), DisplayName("内径 ID [mm]")]
    public double TubeIdMm { get; set; } = 50.0;   // Pt_Heater.3dm: Ø52/Ø50

    // ★★★ 2026-09-03：每段的长度改成在**段表里逐段填**（用户：「每段直接加热铂金管的
    //   长度必须是可以单独设定的」）⇒ 本项降级成「新段的默认值」，不再是所有段的长度。
    //   名字与段表那一列对齐，免得工程师以为是两个不同的量。
    [Category(ParamCat.管几何), DisplayName("直接加热铂金管的长度 [mm]（新段默认值）"),
     Description("新加一段时这一段的默认长度。**每段真正用的长度在下面「分段」表里逐段填** —— "
               + "两者不一样时以段表为准。")]
    public double TubeLengthMm { get; set; } = 300;  // Pt_Heater.3dm

    [Category(ParamCat.程序算出), DisplayName("壁厚 [mm]"),
     Description("⚠ 本项被整线链只用来自 ③ 页控件的 LineCase.WallMm；本项只影响「② 粗算」的单段解接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　校核模式用。设计模式下程序会给出电流密度所需的最小壁厚")]
    public double WallMm { get; set; } = 0.8;

    [Category(ParamCat.页面接管), DisplayName("最小可制造壁厚 [mm]"),
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
    // ★★★ 2026-08-30：以下三项**从工程师的参数表里藏起来**（[Browsable(false)]）。
    //
    //   它们是**开发者开关**，而且自己的说明里就写着「不得用于交付」「打开会改判据的数」。
    //   把这种东西摆在工程师面前是**在界面上放一个陷阱**：勾了照样出数、照样解锁出图，
    //   而算出来的是另一套判据。用户 2026-08-30：「让工程师尽量傻瓜式 UI 操作」。
    //
    //   ⚠ 功能一点没少：三个都由命令行开关设（--sigmat / --gslinear / --basetol-legacy），
    //     做配对对照时照常用。今天给热场加同类开关时用的就是 static 字段（不进参数表）——
    //     那才是这类东西该待的地方。
    [Browsable(false)]
    public string WeldMinSource =>
        Math.Abs(WeldMinThicknessMm - WeldMinDefaultMm) < 1e-9
            ? "默认" : "手动（实际经验）";

[Category(ParamCat.管几何), DisplayName("焊接工艺最小厚度 [mm]"),
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

    [Category(ParamCat.管几何), DisplayName("焊接下界安全系数"),
     Description("用户 2026-08-14：「这是理论计算，可以适当加入安全系数」。\n" +
                 "屈曲判据里有三个经验系数，不确定度并不小：\n" +
                 "  · C=0.2 收缩力系数 —— 在**钢**上标定的，铂上没有标定过\n" +
                 "  · β=2 焊道宽/板厚 —— 常规区间 1.5–3（±50 %）\n" +
                 "  · η_melt=0.35 熔化效率 —— 常规区间 0.3–0.5\n" +
                 "  · **k_b 板屈曲系数才是大头**：四边简支 4.0，一边自由仅 0.43，差 9.3 倍。\n" +
                 "    法兰盘正是「内边焊在管上、外边自由」⇒ 落在偏低那一侧。\n" +
                 "因为 k_b 已按最不利取，本系数只覆盖前三项 ⇒ 2.0 足够。")]
    public double WeldSafetyFactor { get; set; } = 2.0;

    [Category(ParamCat.保温与表面), DisplayName("端部额外保温厚度 mm"),
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

    [Category(ParamCat.保温与表面), DisplayName("端部额外保温的长度 mm"),
     Description("自管两端各算起的长度 mm，在这一段内叠加「端部额外保温厚度」。\n" +
                 "取值参考：冷坑的衰减长度 ℓt ≈ 22 mm，实测剖面在 30 mm 处已恢复到 −0.5 K。")]
    public double EndInsulLengthMm { get; set; } = 30.0;

    [Category(ParamCat.保温与表面), DisplayName("安装姿态")]
    public Orientation Posture { get; set; } = Orientation.Horizontal;

    // ---------- 3 电气 ----------
    [Category(ParamCat.电气), DisplayName("许用电流密度 [A/mm²]"),
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
    [Category(ParamCat.电气), DisplayName("管许用电流密度 [A/mm²]"),
     Description("用户 2026-08-15 现场：一般上限 15；管壁 0.6 时 12 是极限。全档取 12")]
    public double TubeJAllowAPerMm2 { get; set; } = 12.0;

    /// <summary>
    /// ★ 决 103（业主 2026-09-24「以管的最大使用电流密度 J &lt; 11 与 20 °C/h（可控硅）为限」）：**管 J 的使用上限**。
    /// 决 103 起卡交付（稳态「管 J」、升温「① 升温」、升温全程逐点管 J、升温电流上限）用的限值 = min(上一项「管许用电流密度」, 本项)，
    /// 唯一读法 <see cref="TubeJLimitAPerMm2"/>；上一项（08-15 现场，全档 12）照印成对照行「· 管电流密度对原许用值（对照）」。
    /// 改回（<see cref="CriteriaRuleSet"/> = 决103前）时不读本项，限值 = 上一项（与改前逐位相同）。
    /// </summary>
    [Category(ParamCat.电气), DisplayName("管 J 使用上限 [A/mm²]"),
     Description("2026-09-24 定：管的最大使用电流密度 J < 11（升温期与稳态期都卡）。卡交付的管 J 限值取本项与「管许用电流密度」两者的较小值；原许用 12 照印作对照。")]
    public double TubeJUseCapAPerMm2 { get; set; } = CriteriaRules.TubeJUseCapDefault;

    /// <summary>决 103：卡交付的管 J 限值（唯一读法，见 <see cref="CriteriaRules.TubeJLimitOf"/>）。</summary>
    [Browsable(false)] public double TubeJLimitAPerMm2 => CriteriaRules.TubeJLimitOf(this);

    [Category(ParamCat.电气), DisplayName("二次电源型式")]
    public SupplyMode Supply { get; set; } = SupplyMode.AcPhase;

    [Category(ParamCat.电气), DisplayName("直流偏置 [% of I_rms]"),
     Description("交流下应为 0。反并联可控矽触发角不对称会产生直流分量，" +
                 "该分量驱动玻璃电解 → 铂阳极溶解 + 碱迁移。用钳表直流档实测填入")]
    public double DcOffsetPercent { get; set; } = 0.0;

    [Category(ParamCat.电气), DisplayName("玻璃电阻率 [Ω·cm]"),
     Description("工作温度下的熔体电阻率。含碱玻璃 1–10，硼硅/无碱 50–500。仅用于直流分量核算")]
    public double GlassResistivityOhmCm { get; set; } = 5.0;

    [Category(ParamCat.电气), DisplayName("铂溶解价态 z"),
     Description("法拉第定律用。Pt²⁺ 取 2，Pt⁴⁺ 取 4（保守取 2）")]
    public double PtValence { get; set; } = 2.0;

    // ---------- 4 铂表面 / 保温 ----------
    [Category(ParamCat.保温与表面), DisplayName("铂表面发射率 ε"),
     Description("抛光 0.10–0.15，使用后发暗 0.20–0.30。裸管时热损失与本值成正比")]
    public double PtEmissivity { get; set; } = 0.18;

    [Category(ParamCat.保温与表面), DisplayName("保温外表面发射率 ε_out"),
     Description("氧化铝纤维/致密氧化铝约 0.4–0.6")]
    public double OuterEmissivity { get; set; } = 0.45;

    // ★ HANDOVER §4.2l：用户「稳态电流密度远低于 10 A/mm²」，而模型算出管 J≈10.3 顶在限值上；
    //   同一根因还造成玻璃温降 模型 41 K vs 实测 20 K。散热高估 ⇒ 功率大 ⇒ 电流大 ⇒ J 大、抽热大。
    //   本系数是**集总标定量**，不预设高估来自哪一环（发射率？纤维实际更厚？环境不是 25 °C
    //   而是被炉膛包围？氧化铝托管接触？）—— 那些只有现场才能分辨。
    //   实现上对「层导热 + 表面换热」**同倍**缩放，于是各界面温度不变而热流严格 ×LossScale
    //   （只缩表面在纤维热阻主导时几乎无效；只缩最终结果则内外能量不闭合）。
    //   标定方法见 CLI --calib。默认 1.0 = 不标定，与历史结果一致。
    [Category(ParamCat.保温与表面), DisplayName("散热标定系数"),
     Description("整条散热通道的集总标定倍率。1.0 = 模型原值；0.5 = 实际散热只有模型的一半。\n" +
                 "由 --calib 用实测段电流或实测玻璃温降反标定。见 HANDOVER §4.2l")]
    public double LossScale { get; set; } = 1.0;

    // ★ 2026-08-10 按现场实况修正：纤维包覆厚度 **2–3 mm**（原设 10 mm，差 4 倍）。
    //   保温热阻几乎全部由纤维贡献（致密氧化铝 k≈9 W/m·K，其 5 mm 只占总热阻约 1 %），
    //   故这一项直接决定散热量级 —— 改动会连带影响电功率、法兰自给率 Φ 与升温核算。
    [Category(ParamCat.页面接管), DisplayName("① 内层（贴铂）"),
     Description("⚠ 本项被「③ 整线核算」页的「纤维保温 mm」控件接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　现场实测包覆厚度 2–3 mm，取中值 2.5。热阻几乎全在这一层")]
    public InsulationLayer Layer1 { get; set; } = new()
    { Name = "高纯氧化铝纤维", ThicknessMm = 2.5, K0 = 0.04, K1 = 3.0e-4 };

    // ⚠ 实物是**半管套**（仅下半圈，见 Pt_Heater.3dm 图层「氧化铝管」，内 R26/外 R31，Z≤0）。
    //   此处仍按整圈同心层处理 —— 因其热阻只占约 1 %，影响主要在外表面半径与发射率，
    //   量级上可接受；若要精确需改为按包角加权的并联热阻。
    [Category(ParamCat.保温与表面), DisplayName("② 中层"),
     Description("致密氧化铝半管套（实物仅下半圈）。热阻占比 ~1%，此处按整圈近似")]
    public InsulationLayer Layer2 { get; set; } = new()
    { Name = "致密氧化铝半管套", ThicknessMm = 5, K0 = 25.0, K1 = -0.016 };

    [Category(ParamCat.保温与表面), DisplayName("③ 外层（可选）")]
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

    [Category(ParamCat.页面接管), DisplayName("法兰有保温"),
     Description("⚠ 本项被「③ 整线核算」页的「法兰保温」下拉接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　法兰双面包覆高纯氧化铝纤维。降低 q″ 会降低自给所需的厚度")]
    [TypeConverter(typeof(ChineseBoolConverter))]
    public bool FlangeInsulated { get; set; } = true;

    // ★ 2026-08-10 随 Layer1 一并改为 2.5：用户给的「纤维包覆 2–3 mm」是针对铂管的，
    //   此处按「同一材料同一工艺」外推到法兰。**待现场确认**（见 HANDOVER §6 待补数据 ⑦）。
    //   留 10 mm 而管子改 2.5 mm 会物理不自洽：管子保温薄 ⇒ 电流大 ⇒ 同一电流流过裹得厚的法兰
    //   ⇒ 法兰过热、Φ≫1 ⇒ 向管根倒灌，实测算出管根 2137 °C（超铂熔点 1768 °C）。
    [Category(ParamCat.页面接管), DisplayName("法兰保温厚 [mm]"),
     Description("⚠ 本项被「③ 整线核算」页的「法兰保温厚 mm」控件接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　单面厚度，材料取内层①的 k(T)。按与铂管同一包覆工艺取 2.5，待现场确认")]
    public double FlangeInsulThickMm { get; set; } = 2.5;

    [Category(ParamCat.法兰与铜排), DisplayName("法兰吹风风速 [m/s]"),
     Description("压缩空气强制冷却。0 = 仅自然对流。每吹掉一瓦都要由铂金发出来，直接折算成铂重")]
    public double FlangeAirVelocityMPerS { get; set; } = 0.0;



    [Category(ParamCat.法兰与铜排), DisplayName("对流特征长度 [m]"),
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

    [Category(ParamCat.页面接管), DisplayName("铜排夹持温度 [°C]"),
     Description("⚠ 本项被「③ 整线核算」页的「铜排夹持 °C」控件接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　舌片末端整条边的强制温度。<0 = 无夹冷（自由辐射端）。" +
                 "空冷即可，不需要水冷 —— 400 °C 与 80 °C 的差别仅约 10 W。" +
                 "★ 这一项是现场把法兰自给率整定到位的**唯一可调旋钮**（HANDOVER §4.2v）")]
    public double BusbarClampTempC { get; set; } = -1;

    [Category(ParamCat.页面接管), DisplayName("铜排压接长度 [mm]"),
     Description("⚠ 本项被**设计记录**接管：整线链的两条路都强制取 DesignSpec.Current.ClampLengthMm（现为 40 mm，见 DesignSpec.cs 与 LineDesignPage.PageToDesignSpec）—— 在这张表里改它，对「③ 整线核算」没有影响。本表显示的 3 mm 只对「② 粗算」的单段解有效。　沿舌片方向的压接长度，即定温边界的深度。\n" +
                 "★ 早先在网格里硬编码为 3 mm —— 那是**数值边界，不是设计值**：\n" +
                 "  3 mm × 舌宽 40 mm = 120 mm² 接触面，共用片 1099 A ⇒ 界面电流密度约 9 A/mm²，\n" +
                 "  而铜排压接通常按 ≤1 A/mm² 量级设计，差一个数量级 —— 现场做不出来。\n" +
                 "加长它会把定温边界推向圆盘、缩短导热路径 ⇒ 铜排带走的热增加，**必须重算**。")]
    public double BusbarClampLengthMm { get; set; } = 3.0;

    [Category(ParamCat.法兰与铜排), DisplayName("铜排总热导 [W/K]"),
     Description("填「自动」（或留空）= 程序按铜排到冷端长度、许用电流密度与铜的导热自己算；填一个 ≥ 0 的数就用你给的。\n" +
                 "★ 铜排到冷端的总热导 G = k_Cu·A/L（含铜排自身表面散热）。\n" +
                 "≥0 时**取代** BusbarClampTempC 的二选一，改用第三边界：q = G·(T_舌端 − T_冷端)。\n\n" +
                 "为什么必须有它：此前舌端只有两种边界，而**两种都不是真的**——\n" +
                 "  · 定温：假设铜排无论要带走多少热都能把接触点按在 300 °C，等于假设结论；\n" +
                 "  · 自由端：假设铜排完全不导热。实算给出舌端 1100–2400 °C，\n" +
                 "    而**铜的熔点只有 1085 °C** ⇒ 该工况下接头根本不存在。\n" +
                 "  §4.3c/§4.3d 的「端片自由端」结论正是建立在后者上。\n\n" +
                 "典型值：40×21.8 mm² 铜排、到冷端 300 mm ⇒ G = 385×873e-6/0.3 ≈ 1.1 W/K。\n" +
                 "把 G 当设计变量，接头温度就从**假设**变成**输出**，可以拿去对铜的许用温度。")]
    [TypeConverter(typeof(AutoOrValueConverter))]
    public double BusbarConductanceWPerK { get; set; } = -1;



    [Category(ParamCat.法兰与铜排), DisplayName("铜排许用电流密度 [A/mm²]"),
     Description("★ 铜排截面的第一性原理来源：A = I / J许用。自然对流 1.5–2，强制风冷 3–4。　" +
                 "2.0 与 `--cli --busbar` 选型表用的是同一个数（此前那张表把它写死在调用里）。　" +
                 "⚠ 它与「铜排到冷端长度」一起决定**逐片**热导 G = k_Cu·A/L —— " +
                 "四片电流不同（实测 685/1099/975/542 A），G 本来就该逐片不同。")]
    public double BusbarJAllowAPerMm2 { get; set; } = 2.0;

    [Category(ParamCat.法兰与铜排), DisplayName("铜排到冷端长度 [mm]"),
     Description("★ 从压接点到冷端（散热器/环境）的铜排长度。与许用电流密度一起定 G = k_Cu·A/L。　" +
                 "300 与 `--cli --busbar` 选型表同源。**待现场确认**：实际走线长度决定它。")]
    public double BusbarLenToSinkMm { get; set; } = 300.0;

    [Category(ParamCat.法兰与铜排), DisplayName("铜排冷端温度 [°C]")]
    public double BusbarSinkTempC { get; set; } = 25;


    [Category(ParamCat.法兰与铜排), DisplayName("共用片抽热两侧均分"),
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
    [TypeConverter(typeof(ChineseBoolConverter))]
    public bool SplitSharedFlangeDraw { get; set; } = false;

    [Category(ParamCat.法兰与铜排), DisplayName("基线收敛判据与主环同口径"),
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
    [Browsable(false)]
    [TypeConverter(typeof(ChineseBoolConverter))]
    public bool BaselineTolAmplified { get; set; } = true;

    [Category(ParamCat.法兰与铜排), DisplayName("电位场用旧的 Gauss–Seidel"),
     Description("★ 只为**配对对照**（2026-08-29）。　" +
                 "电位场已改用 **CG + Jacobi 预条件**：旧的 Gauss–Seidel 在泊松型问题上" +
                 "迭代次数 ~ 条件数 ~ h⁻² ~ n ⇒ 总成本 ~ n²；实测单元 6.4× 而每轮耗时 96×。　" +
                 "而且旧解的**收敛判据拿的是「步长」不是残差** —— 与基线耦合环、" +
                 "CoupledSolver 那两处同一个错，慢收敛时步长很小而残差很大。　" +
                 "⚠ 打开只用于搞清楚「哪一改动把 ③ 改了」，**不得用于交付**。")]
    [Browsable(false)]
    [TypeConverter(typeof(ChineseBoolConverter))]
    public bool LinearGaussSeidel { get; set; } = false;



    [Category(ParamCat.法兰与铜排), DisplayName("电流场按 σ(T) 重解"),
     Description("★ 第一性原理开关（2026-08-28）。　" +
                 "ShellCurrent.Solve 的 tempC 形参就是为 σ(T) 造的，机制写对了，" +
                 "但全仓 12 个调用点没有一个传过它 ⇒ 交付用的整线链恒按等温解电流场。　" +
                 "同一片法兰上管孔 1150 °C、压接段 450 °C、舌片可超 1400 °C，" +
                 "ρe(450)/ρe(1150) ≈ 0.45 ⇒ 冷区更导电、电流往那头挤，等温模型看不见 —— " +
                 "而 ②″ 判的正是局部电流拥塞造成的峰值。　" +
                 "★ 项目做对过：已被取代的 CoupledSolver 每轮都用新温度场重解电流场" +
                 "（注释：铂 700–1300 °C 间 ρe 变化 48 %），重写时丢掉了。　" +
                 "⚠ 默认关：打开会改判据的数（②″ 与局部热稳定首当其冲）。")]
    [Browsable(false)]
    [TypeConverter(typeof(ChineseBoolConverter))]
    public bool SigmaOfTCoupling { get; set; } = false;

    // ---------- 6 玻璃物性 ----------
    [Category(ParamCat.玻璃物性), DisplayName("密度 [kg/m³]")]
    public double GlassDensity { get; set; } = 2500;

    [Category(ParamCat.玻璃物性), DisplayName("比热 [J/kg·K]")]
    public double GlassCp { get; set; } = 1300;

    [Category(ParamCat.玻璃物性), DisplayName("内壁换热系数 hg [W/m²K]"),
     Description("由层流 Nu≈3.66 与玻璃熔体导热 k≈0.9 W/m·K 得 hg = Nu·k/D ≈ 65（ID50）。\n" +
                 "旧默认值 220 对应 k_eff≈2.2，远高于熔体导热，会把玻璃向管壁的放热放大约 3.4 倍，\n" +
                 "使 --glass 的全程温降算成 67.6 K 而实测仅 20 K。改动依据见 --glass 的反解。")]
    public double HGlass { get; set; } = 65;

    [Category(ParamCat.玻璃物性), DisplayName("粘度 [Pa·s]"), Description("压降估算用")]
    public double GlassViscosity { get; set; } = 30;

    [Category(ParamCat.法兰与铜排), DisplayName("法兰抽热覆盖 [W]"),
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
    [TypeConverter(typeof(ChineseBoolConverter))]
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

    /// <summary>
    /// ★ R48 审查第 5 条（2026-09-14，Opus 5）：**端部额外保温渐变形状**用的管内玻璃换热 hg W/(m²·K)。&lt; 0（默认 −1）= 取 <see cref="HGlass"/>，逐位不变。
    ///
    /// 病：渐变形状按 ℓt = √(kA/β) 定，β 含 hg·π·D。空管工况把 hg 置 0 ⇒ ℓt 变长（B2 上 24.8 → 38.5 mm）⇒ 装上去的保温厚度分布跟着工况变
    ///   （30 mm 端区最外子区间的额外厚度 exp(−27.5/24.8) = 0.33 倍 → exp(−27.5/38.5) = 0.49 倍）—— 等于换了工况就换了一套硬件。
    /// 修：形状按**带玻璃**的 hg 定。LineRunner 的空管段把置 0 之前的 hg 写到这里；单段页上人自己填 hg = 0 时没有「带玻璃那一份」可取，照旧按本身参数。
    /// 残余差异：β 的散热切线斜率取自损失表（三次样条，表的温度上界随玻璃进口变），两工况只差样条插值，量级远小于打印精度。
    /// 用 −1 当「未设」：hg 物理上 ≥ 0，负值不会是合法输入（同 <see cref="BusbarConductanceWPerK"/> 的 −1 = 自动）；不用 NaN —— 参数表存盘的 JSON 不收 NaN。
    /// </summary>
    [Browsable(false)] public double EndInsulShapeHGlass { get; set; } = -1;

    /// <summary>
    /// ★ R48 G3（2026-09-15，Opus 5；物理把关人第十一轮「空管管腔轴向辐射进模型」）：**空管管腔轴向辐射**的比例系数，
    /// 以 <see cref="SegmentSolver.CavityRadRefC"/>（1150 °C）时的等效轴向导热 kA 表示，W·m/K。默认 0.023。
    /// 前身是 R48 审查第 3 条（2026-09-14）的敏感度钩子 TubeCavityRadKAWmPerK（默认 0、带玻璃也加、不随温度）；改名是为了让旧语义的读者编译不过，不会读错。
    ///
    /// ══ 物理（出处：物理把关人第十一轮；下面量级是 2026-09-14 仓库里的推理估算，未实测）
    /// 空管时管腔是连续的高温腔体，腔内辐射沿轴向传热，相当于附加的轴向导热 kA_rad(T) ∝ T³；带玻璃时腔被玻璃充满，没有这条路（取 0）。
    ///   黑体长管：等效 kA = 8σT³·πD·R²·∫₀^∞ H·F(H) dH = (16/3)σT³·D × 管腔截面（F = 管壁微元环对同轴圆盘的角系数，积分 = 2/3），
    ///   1100 °C、Ø50 ⇒ 0.077 W·m/K（1150 °C ⇒ 0.086），是管壁 kPt·A（83.3 × 1.28e-4 ≈ 0.0106）的约 7～8 倍；
    ///   灰体 ε = 0.18（<see cref="PtEmissivity"/>）在尺度 ℓ 上与表面热阻串联：kA_eff = 1/(1/0.077 + 1/(G_s·ℓ²))，G_s = πD·ε/(1−ε)·4σT³ ≈ 20 W/(m·K)
    ///   ⇒ ℓ = 40 mm 时约 0.023（管壁的 2.1 倍）；与 ℓ 自洽迭代（ℓ = √(kA_总/hP)，空管 hP ≈ 6.7）收在约 0.056（5.2 倍，ℓ ≈ 99 mm）。
    ///   ⚠ 这两档是按 1100 °C 算的；物理把关人第十一轮定「在 1150 °C 下标定」⇒ 本系数把它们当 1150 °C 的值用（同一式在 1150 °C 重算低档是 0.025，差在两档之间的不确定度以内）。
    /// ══ 默认值：暂取低档 0.023，**两档之间没有先验依据取舍**，待物理把关人定（2026-09-15 Opus 5 审查后改写）
    ///   ⚠ 本段初版（同日）写的是「两档都出自扩散近似、扩散近似偏大 ⇒ 两档都偏上限 ⇒ 取较小的那档离真值近」—— **这推不出来，已撤回**：
    ///     两档用的是同一个扩散近似的黑体值 0.077，差别只在尺度 ℓ。低档的 ℓ = 40 mm 是随手取的，与同一估算的自洽尺度不符：
    ///     √((0.0106 + 0.023)/6.7) = 71 mm，代回得 0.043，再迭代收到 0.055（ℓ ≈ 99 mm，awk 按上面两式重算，2026-09-15 Opus 5）⇒
    ///     **在这个估算内只有高档自洽**；低档偏低是因为 ℓ 取得不自洽，不是修正了扩散偏差。扩散近似在 ℓ ≈ 1～2 D 时让两档一起偏大，偏多少未知，不偏向哪一档。
    ///   ⇒ 默认值只是沿用本路初版的取值，不代表更可信；在物理把关人定之前，**空管结论一律两档并报**
    ///     （B2 设计两档续跑的并列见 deliverable\R48_空管续跑_两档并列_2026-09-15_&lt;时刻&gt;.txt 里读同一编译产物那一对的一份；
    ///      05:03 那份不带时刻，读的是更早的编译产物，数与之逐位相同但出处不同 —— 第二轮审查后改，2026-09-15 Opus 5）。
    ///   没有「偏保守」的一档：R48 C 路开箱网格三档对照（deliverable\R48_空管稳态_空管_生产设定_开箱网格_腔辐射kA*，r48_C 树）里
    ///   kA 变大时下游片超温变小、入口片超温变大，方向不一致。
    ///   ⚠ 本属性会随界面「保存方案」写进方案文件（MainForm.Save 把全部公开属性序列化），读方案时照读，界面参数表却不显示它（Browsable(false)）⇒
    ///     以后默认值若改（比如定成高档），旧方案读进来仍是存盘时的值，参数表上看不出来；整线结果的空管工况说明会印出系数；单段解的 SolveResult.Note 也写（SegmentSolver.EmptyTubeNoteOf），
    ///     单段页报表空管时把它印成「工况说明」一节（MainForm.Report；第二轮审查后加，2026-09-15 Opus 5，原来单段页不显示 Note）。
    ///     不能加 JsonIgnore：<see cref="SegmentSolver.Clone"/> 靠 JSON 往返复制，加了会丢值。改默认值的人要一并处理读方案（交接 open issue）。
    /// ══ 口径（写死在 <see cref="SegmentSolver.CavityRadKA"/>，只此一处）
    ///   · 温度依赖：按**段控温点温度取常数** kA_rad = 本系数 × ((T_set + 273.15)/(1150 + 273.15))³ —— 与管壁 kPt 取 T_set 同一口径，
    ///     一维求解器的 K 只随位置不随温度；沿管实际温度偏离控温点的部分不跟。
    ///     误差量级按当前模型（合并 A/D 之后、含管腔辐射、全线 1150 空管、B2 设计）：低档管根 1117.95～1173.32 °C ⇒ T³ 相对控温点 −6.6 %～+5.0 %，
    ///     高档 1129.13～1164.43 °C ⇒ −4.3 %～+3.1 %（出处 deliverable\R48_空管续跑_kA0.023_2026-09-15.txt／…_kA0.056_2026-09-15.txt 续跑末轮逐段管根，awk 算三次方；
    ///     带时刻 _064409 的两份重跑与之 DATA 行除编译产物号外逐位相同，md5 核对，2026-09-15 Opus 5）；
    ///     两档之间差 2.4 倍，这一项小于它。（初版引的「管根最高 1254 °C、T³ 差 24 %」出自 r48_C 合并前的模型、无管腔辐射，不是当前模型，已换掉。）
    ///     ⚠ 2026-09-15 Opus 5（合并，复审后改）：出处层次 —— 本段引的 R48_空管续跑_* 各份（kA0.023／kA0.056、两档并列）都是在 r48_G3 工作树上跑的（底板 460d3b，即 **F 合入前的配方**：
    ///       压接形心整格 + 3·hFine 细带）；合并树（F 压接面上定温、缺省不铺细带）下没有重跑，管根 1117.95～1173.32 °C 等数只读作那份配方下的；
    ///       文件首跑在 r48_G3 工作树；2026-09-15 Opus 5（I 路）：kA0.023／kA0.056／两档并列各份（含带时刻的重跑）已原样拷入本树 deliverable（md5 与源相同，清单 deliverable/R48_合并拷入证据清单_2026-09-15.txt），原句「合并树里没有」作废。
    ///   · 只在空管（<see cref="SegmentSolver.IsEmptyTube"/>）时有；带玻璃为 0（带玻璃逐位不变）。
    ///   · 段间接头：两段管腔在接头处连通（整线只封住两头）⇒ 接头导度也加这一份，按**邻段端温**的 T³ 取；
    ///     收敛后同一接头两侧管腔那一份只差 O(3ΔT/T)，**前提是两段节距 Δx 相同** —— 接头导度按本段自己的节距（段长 ÷ (节点数 − 1)）取，
    ///     两段不等长（节点数同为 401）时两侧导度差节距比，接头热流按同一比例失配；管壁那一份原来就有这个问题，管腔那一份跟着有（交接 open issue，
    ///     R48CavityRadiationGateTests 接头门里有不等长对照的数）。
    ///   · 管口：铂管两端封住 ⇒ 不计管口向外辐射（整线两头的端部边界照旧只有法兰抽热）。
    ///   · 标定只对 Ø50 管腔、ε = 0.18：本系数**不按管径、发射率换算**；空管工况说明在两者与标定不同时写明。
    ///   · 不进端部额外保温的形状（那是硬件，按带玻璃算，见 <see cref="EndInsulShapeHGlass"/>）。
    ///   · **没进升温模型**（本轮范围只到 SegmentSolver）：RampSolver 是集总单节点，没有轴向导热；但 <see cref="RampTwoNode"/> 的管—法兰耦合导度
    ///     用半无限翅片入口导度 √(kPt·A·β)，**含管壁轴向导热**，而升温期管里没有玻璃、管腔辐射在物理上同样存在（按两档 kA 管侧翅片导度约大 √3.2 ≈ 1.8／√6.3 ≈ 2.5 倍）。
    ///     读它的地方（2026-09-15 Opus 5 逐处查过）：整线参考量「升温期法兰−管峰值」（LineRunner.FlangeLumped → RampTwoNode.Solve）、
    ///     设计电流里「对照·两节点含法兰自热」那一栏（DesignCurrent 调 RampTwoNode.Solve，只印、不进尺寸链）；
    ///     硬判据「升温」走管子准静态电流 RampTwoNode.QuasiStaticCurrentA，不经这个耦合导度。带玻璃算例也算这两处参考量，改了会动已记录的参考值
    ///     ⇒ 要不要计入交物理把关人定（交接 open issue）。
    ///     （初版此处写「RampSolver／RampTwoNode 没有轴向梯度」，对 RampTwoNode 是错的，2026-09-15 Opus 5 审查后改。）
    /// </summary>
    [Browsable(false)] public double TubeCavityRadKA1150WmPerK { get; set; } = SegmentSolver.CavityRadKA1150Low;

    [Category(ParamCat.程序算出), DisplayName("壁厚由程序反算"),
     Description("⚠ 本项被LineRunner 强制置 false —— 整线链壁厚由 LineCase.WallMm 定，自行反算会与之打架接管 —— 在这张表里改它，对「③ 整线核算」没有影响。　关闭 = 校核模式：壁厚取「最小可制造壁厚」的实测值，程序只报实际 J")]
    [TypeConverter(typeof(ChineseBoolConverter))]
    public bool SizeWall { get; set; } = false;

    // ---------- 7 数值 ----------
    [Category(ParamCat.数值), DisplayName("网格节点数"),
     Description("二阶格式。网格收敛测试显示 n=201 在冷点温度上约 2 K 离散误差，n=401 约 0.5 K")]
    public int Nodes { get; set; } = 401;

    // ★★★★★ R48 M（2026-09-18，Fable 5.1）：**段解地板三项**（从工程师参数表里藏起来：是求解器内部的数值分辨率，不是工况）。
    //
    //   病（实测出处 deliverable/R48_M_停机容差阶梯_两点_本次开跑于2026-09-18_214343.txt 与同名「进度活页」）：外层耦合的停机容差改成绝对目标之后，
    //   两个「轮数彩票」点在常数 0.05 K 上一个 91 轮、另一个 271 轮还没停 —— 40 轮以后真残差停在 0.0013～0.0029 K、步长在 0.006～0.027 K 之间打转，
    //   乘放大 35.9 就是 0.05～0.1 K／0.2～1 K：**这是段解自己的分辨率地板**，与容差口径无关。
    //   地板的来源：段电流按二分求到 xTol = 0.05 A 就停（Roots.Monotone 返回括号中点 ⇒ 电流落在 0.025 A 的格子上；1150 °C 附近 dT/dI ≈ 1.9 K/A ⇒ 管温跳 0.05 K 一格），
    //   外层 Picard 停在 0.01 K、内层 Bvp1D 停在 0.005 K。§0.-10 ⑤ 早写着「要再往下收一个数量级时先量它」——本轮量了，也降了。
    //   降 10 倍（电流 0.05 → 0.005 A、Picard 0.01 → 0.001 K、Bvp1D 0.005 → 0.0005 K）；成本与效果见 HANDOVER §0.-16M（阶梯前后）。
    //   ⚠ 只改数值分辨率，判据的物理口径与限值一个字没动；门 R48MStopTolGateTests.门_段解地板降了十倍_注射旧地板当场红。
    [Browsable(false)]
    public double SegCurrentTolA { get; set; } = SegCurrentTolADefault;
    [Browsable(false)]
    public double SegPicardTolK { get; set; } = SegPicardTolKDefault;
    [Browsable(false)]
    public double SegBvpTolK { get; set; } = SegBvpTolKDefault;
    /// <summary>R48 M（2026-09-18，Fable 5.1）：段解地板的生产默认（新）与改前的旧值（门注射用）。</summary>
    public const double SegCurrentTolADefault = 0.005, SegPicardTolKDefault = 0.001, SegBvpTolKDefault = 0.0005;
    public const double SegCurrentTolALegacy = 0.05, SegPicardTolKLegacy = 0.01, SegBvpTolKLegacy = 0.005;

    /// <summary>
    /// ★ 2026-09-23（SEG，决 97 A「段电流连续根」；业主 2026-09-23「其余先按路线甲」）：段电流二分**收尾返回连续根**（末括号内线性插值，
    /// <see cref="Roots.MonotoneContinuous"/>），不再返回括号中点。二分本身（xTol = <see cref="SegCurrentTolA"/>、求值次序）一位没动。
    /// 病与归因：deliverable/R48_耦合轮数归因_段电流二分格彩票_2026-09-23.md（中点 ⇒ 段电流落在约 2.6e−3 A 一格 ⇒ 外层耦合 G(x) 分段常数 ⇒ 停机轮数彩票 29 → 60 → 119）。
    /// **改回参数**：生产不设（= true，连续根）；false = 老中点，与改前逐位相同（门 R48SegContinuousRootTests）。只给门与「开 − 关」归因用。
    /// 放在参数表对象上是因为段解只看得到它，且参数表复制（Clone 走 JSON）会带着它 —— 与段解地板三项同一做法。
    /// </summary>
    [Browsable(false)]
    [TypeConverter(typeof(ChineseBoolConverter))]
    public bool SegCurrentContinuousRoot { get; set; } = true;

    /// <summary>
    /// ★★★★★ 决 103（业主 2026-09-24）：**判据口径总开关**（见 <see cref="PtOptimize.Core.CriteriaRuleSet"/>）。
    /// **改回参数**：生产不设（= 决103）；决103前 = 2026-09-14 口径，整线判据、求解器分派、升温全程判定与改前逐位相同（门 R48CriteriaSwapGateTests）。只给门与「开 − 关」归因用。
    /// 放在参数表对象上的理由与 <see cref="SegCurrentContinuousRoot"/> 相同（整线判据、求解器、升温全程都只看得到它，Clone 走 JSON 带着它）。
    /// </summary>
    [Browsable(false)]
    public CriteriaRuleSet CriteriaRuleSet { get; set; } = CriteriaRuleSet.决103;

    /// <summary>
    /// ★ 2026-09-25（决 103 口径下的补充；改回 = false）：整片热稳定的夹持导度在稳态场标定不出来、且原因是法兰均温不比夹持参考温度高出
    /// <see cref="RampTwoNode.CalibMinDeltaK"/>（铜排在给法兰加热，「热 ÷ 温差」没有物理意义）时，改按舌片导热截面的几何导度算这一项
    /// （<see cref="FlangeStability.Result.DClampGeomDT"/>，FlangeStability 一直算着的那份；比场标定值偏小 ⇒ 裕度偏保守）。
    /// 起因：端到端 W08（deliverable/R48_L_端到端_细网格_W08_本次开跑于2026-09-24_224007.txt）升温期 300 °C 点（夹头 450 °C）整片热稳定判不了，把 ① 整段判成判不了。
    /// 只在 <see cref="CriteriaRuleSet"/> = 决103 下生效；决103前不判升温期热稳定、稳态也照旧判不了（逐位同改前）。
    /// 标定失败的其他原因（本片没有场、带走的热为负而温差够）仍判不了。判定在 <see cref="LineRunner.GeomClampFallback"/>。
    /// </summary>
    [Browsable(false)]
    [TypeConverter(typeof(ChineseBoolConverter))]
    public bool FlangeStabGeomClampFallback { get; set; } = true;

    /// <summary>
    /// ★★★★★ 决 104（业主 2026-09-25 原话「圆盘保温块最大厚度(圆盘之前说过了10mm)」；出处 = 业主 2026-09-17 原话「现场法兰与管保温带一般缠绕20圈以下，再往上很难缠绕」，每圈 0.5 mm ⇒ 10 mm，HANDOVER §0.-11）：
    /// 圆盘保温厚度**上限** mm = 判据「接合区保温缠得出来」在 决103 口径下的限值（<see cref="WrapLimits.Judge(LineCase, CriteriaRuleSet)"/>，硬安全线）。
    /// **不封顶、不外推、不按上限硬算**：读值口径 <see cref="LineCase.DiscInsulEffectiveAt"/> 仍回设定值（配套清单、热解、判据同源），超上限 = 不可行并写明杠杆。
    /// 2026-09-18 曾把本条降为参考行（默认 20、界面上界 60，§0.-12／§0.-15M）；2026-09-25 业主重申 10 mm 是最大厚度 ⇒ 升回硬判据。设计记录默认 <see cref="DesignSpec.FlangeInsulMm"/> 20 未动（改则全套钉数重录，决 46）。
    /// 改回 = <see cref="double.PositiveInfinity"/>（回到参考行，逐位同 2026-09-18 口径）；决103前 口径不读本项。
    /// </summary>
    [Browsable(false)]
    public double DiscInsulCapMm { get; set; } = 10.0;

    // ---------- 派生 ----------
    [Browsable(false)] public double TubeId => TubeIdMm * 1e-3;
    [Browsable(false)] public double TubeLength => TubeLengthMm * 1e-3;
    [Browsable(false)] public double Wall => WallMm * 1e-3;
    [Browsable(false)] public double JAllow => JAllowAPerMm2 * 1e6;   // A/m²
    /// <summary>升温集总模型（RampSolver）的管电流上限 A/m²。决 103：读卡交付的管 J 限值（<see cref="TubeJLimitAPerMm2"/>）；改回时 = 管许用电流密度（逐位同改前）。</summary>
    [Browsable(false)] public double TubeJAllow => TubeJLimitAPerMm2 * 1e6;   // A/m²
    [Browsable(false)] public double GlassRho => GlassResistivityOhmCm * 1e-2;  // Ω·m
    [Browsable(false)] public double MassFlow => ThroughputTPerDay * 1000.0 / 86400.0; // kg/s

    [Browsable(false)]
    public List<InsulationLayer> Layers => new() { Layer1, Layer2, Layer3 };
}
