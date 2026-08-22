using System;
using System.Collections.Generic;
using System.Linq;

using PtOptimize.Core;

namespace PtOptimize.UI;

// ═══════════════════════════════════════════════════════════════════════════
//  Flow —— 「阶段 · 链 · 命令 · 参数」的**单一数据源**
// ═══════════════════════════════════════════════════════════════════════════
//
// 为什么要有这个文件（2026-08-20，用户：「目前的 UI 界面太乱了，有好几种算法链条，
// 可以依照算链条分类 —— 现在是所有标签键都可以点，工程师根本不知道自己目前在算什么」）：
//
// 界面里同时跑着六条计算链，它们回答的问题不同、耗时差四个数量级、权威性完全不同
// （只有 C 整线耦合可交付），而界面把它们平铺成五个随时可点的页签，**没有任何东西**
// 告诉你手上这个数字是哪条链算的。这不是审美问题，是正确性问题。
//
// 更要命的是这件事**没有机器守着**：ManualPage 的界面地图是手写 SVG，页签名与按钮名
// 硬编码在里面，它的类头自己写着「改了页签或工具条，必须回来改这里。图与界面漂开时
// 没有任何东西会报错」。—— 一份靠人记得去同步的文档，就是一份迟早会骗人的文档。
//
// ⇒ 把结构抽成这一份数据，四方都从它读：
//      ① 界面构建（MainForm 建页签、各页建工具条）
//      ② 门禁（Gate.Evaluate）
//      ③ 说明书（ManualPage 的 SVG 图与按钮表）
//      ④ 接线测试（tests/UiWiring，**双向**断言）
//
// ⚠ public，不是 internal：接线测试要直接读它。
//   TextFmt 是 internal，于是测试只能反射进去（tests/UiWiring/Program.cs:502）——
//   那层反射本身就是一处会静默漂开的接缝（改个方法名，测试悄悄取到 null 而不是编译失败）。
//   这里不重蹈。
//
// ⚠ 本文件**只有数据，没有行为**。命令的实现仍在各页；这里登记的是
//   「它叫什么、属于哪条链、归哪个阶段、读不读页面控件」。

/// <summary>六条计算链。名字里带链号，报告与状态面板直接印。</summary>
public enum ChainId
{
    /// <summary>单段解析（SegmentSolver.Solve）—— 不含法兰</summary>
    A单段解析,
    /// <summary>单段参数扫描（SegmentSolver.Sweep）</summary>
    A单段扫描,
    /// <summary>分段解析强度与铂重（LineSolver.Solve/Totals）—— 不解温度场</summary>
    B分段解析,
    /// <summary>分段法兰定尺（LineSolver.SizeFlanges → CoupledSolver）</summary>
    B分段法兰,
    /// <summary>★ 整线耦合解（LineRunner.Run）—— 判据的唯一来源，唯一可交付</summary>
    C整线耦合,
    /// <summary>自动定厚（Sizer.Solve / FlangeAutoSizer.SolveByLevel）—— C 的外层</summary>
    C定尺寸,
    /// <summary>形状搜索（Sizer.Solve × N 个形状）—— C 的最外层</summary>
    C形状搜索,
    /// <summary>升温可达性闭式快筛 —— 前置闸门，不调求解器</summary>
    D升温闸,
    /// <summary>不属于任何计算链：存档、出图、载入</summary>
    无,
}

/// <summary>
/// 阶段轨的五格，外加末尾的「使用说明」。顺序即阅读顺序；门禁只加在物理上真有先后的地方。
///
/// ⚠ 说明页也算一格 —— 不是因为它是一个阶段，而是因为**每个页签都必须在这张表里有位置**。
///   把它特判成「表外的例外」，接线测试的双向断言就得跟着开一个口子，
///   而那个口子迟早会被第二个、第三个例外挤大。它没有门（前一格 GateToUnlockNext 为 null）
///   ⇒ 天然永远解锁，正合「说明书任何时候都该能看」。
/// </summary>
public enum StageId { 定案档, 先决条件, 粗算, 整线核算, 定尺寸, 交付, 说明 }

/// <summary>
/// 命令的三组分法 —— 沿用说明书里已经教给用户的那套（ManualPage §2.3）。
/// 那里写着：「「定案」两个字打头的那几个**不读页面上的控件**……中间三个**读页面控件**」。
/// </summary>
public enum CmdGroup
{
    /// <summary>「定案」打头：只认 FinalDesign，页面上改什么都影响不了它们 ⇒ **不受阶段门禁**</summary>
    定案不读页面,
    /// <summary>读当前页面控件：算的是你现在填的这组参数</summary>
    页面参数,
    /// <summary>工具：只测不调</summary>
    工具,
    /// <summary>出图与存档</summary>
    导出,
}

// ───────────────────────────────────────────────────────────────────────────

/// <param name="EntryPoint">求解器入口方法名 —— 状态面板与说明书都直接印它，
/// 让「我现在在算什么」有一个可以拿去 grep 的答案。</param>
/// <param name="Deliverable">能否作为交付数。**只有 C整线耦合 是 true。**</param>
public sealed record ChainSpec(
    ChainId Id,
    string Name,
    string Answers,
    string EntryPoint,
    string Cost,
    bool Deliverable);

/// <summary>
/// 解锁**下一关**的条件。
///
/// ⚠⚠ 门禁**只读判据，绝不产生判据**（HANDOVER §1.8 铁律三：「判据只有一个来源
///   …… 不得自行重算 —— 散在三处正是连错四次的根源」）。
///   所以 <see cref="RequiredChecks"/> 只准填 <see cref="LineResult.Key"/> 里的常量，
///   不准写字符串字面量 —— 判据改名时编译期就会断，而不是门禁悄悄永远放行。
/// </summary>
public sealed record GateSpec(
    string[] RequiredChecks,
    bool RequireConverged,
    bool RequireAllOk,
    bool RequireFresh,
    string LockedTitle,
    string LockedWhy);

/// <param name="Id">稳定锚点（如 "core.runLine"）。测试与说明书按它引用，
/// 于是**改中文名不会打破任何东西**。</param>
/// <param name="Text">界面按钮上的字 —— 说明书按钮表用的是**同一个字符串**。</param>
/// <param name="ReadsPageControls">false ⇒ 不受阶段门禁（「定案」那四个）。</param>
public sealed record CommandSpec(
    string Id,
    string Text,
    StageId Stage,
    ChainId Chain,
    CmdGroup Group,
    bool ReadsPageControls,
    string Cost,
    string Tip);

/// <param name="CategoryPrefix">与 DesignInputs 的 [Category] 前缀**逐字**对应。
/// 分类名是编译期常量 ⇒ 天然单一来源。</param>
/// <param name="OverriddenBy">非空 = 这一项在整线链上**被别处接管了，在参数表里改它没用**。
/// 空字符串 = 真正生效。</param>
public sealed record ParamScope(
    string CategoryPrefix,
    ChainId[] EffectiveIn,
    string OverriddenBy);

public sealed record StageSpec(
    StageId Id,
    int Order,
    string Title,
    string Banner,
    ChainId[] Chains,
    GateSpec? GateToUnlockNext,
    string[] CommandIds,
    string[] ParamCategoryPrefixes);

// ───────────────────────────────────────────────────────────────────────────

public static class Flow
{
    // ═══ 链 ═══════════════════════════════════════════════════════════════
    public static readonly ChainSpec[] Chains =
    {
        new(ChainId.A单段解析, "A 单段解析", "一根管的热平衡/电气/铂重，**不含法兰**",
            "SegmentSolver.Solve", "秒级", false),
        new(ChainId.A单段扫描, "A′ 单段扫描", "单个参数扫一条线，看趋势",
            "SegmentSolver.Sweep", "秒级", false),
        new(ChainId.B分段解析, "B 分段解析", "逐段强度与铂重，**不解温度场**",
            "LineSolver.Solve", "即时", false),
        new(ChainId.B分段法兰, "B′ 分段法兰", "把法兰算进总铂",
            "LineSolver.SizeFlanges", "分钟级", false),
        new(ChainId.C整线耦合, "C 整线耦合 ★", "**权威判据表**：能不能造、能不能用、要多少铂",
            "LineRunner.Run", "分钟级", true),
        new(ChainId.C定尺寸, "C′ 定尺寸", "自动调厚度，使判据过",
            "Sizer.Solve", "更久", false),
        new(ChainId.C形状搜索, "C″ 形状搜索", "连盘径与舌宽一起搜，挑最轻的全过解",
            "Sizer.Solve × N", "几十分钟", false),
        new(ChainId.D升温闸, "D 升温闸", "① 空管能不能在期限内升到目标温度（闭式快筛）",
            "Insulation.CylinderLoss（闭式）", "毫秒", false),
        new(ChainId.无, "—", "不算东西：存档、载入、出图", "—", "即时", false),
    };

    // ═══ 命令 ═════════════════════════════════════════════════════════════
    //
    // ⚠ 这张表登记的是**按钮**（ToolStripButton）。ToolStripLabel / ComboBox /
    //   ProgressBar / Separator 不登记 —— 它们没有「执行哪条链」这回事。
    //   接线测试的双向断言也只覆盖按钮，理由同上。
    public static readonly CommandSpec[] Commands =
    {
        // ── ① 闸门 ─────────────────────────────────────────────────────
        new("gate.ramp", "① 升温可达性", StageId.先决条件, ChainId.D升温闸,
            CmdGroup.页面参数, true, "毫秒",
            "5 档保温 × 4 档壁厚，闭式算升温时间与电流密度。**这是快筛，① 的交付判定在「③ 整线核算」给**"),

        // ── ② 快筛（解析·不可交付）──────────────────────────────────────
        new("calc.segment", "计算 (F5)", StageId.粗算, ChainId.A单段解析,
            CmdGroup.页面参数, true, "秒级",
            "按左侧参数表解一次单段。**不含法兰** —— 报告里没有法兰的任何一项"),
        new("sweep.insul", "扫描：保温厚度", StageId.粗算, ChainId.A单段扫描,
            CmdGroup.页面参数, true, "秒级", "内层保温 0–50 mm 扫 11 点"),
        // ⚠ 这里曾有第三个扫描「扫描：法兰厚度」，2026-08-20 删除：
        //   它传的量名 "flangeTf" 在 SegmentSolver.Sweep 里不存在，11 行全是同一个基准解。
        //   A 链结构上不含法兰 ⇒ 补 case 只能是假的。权威答案是 ④ 的「② 厚度灵敏度」。
        new("sweep.eps", "扫描：铂发射率", StageId.粗算, ChainId.A单段扫描,
            CmdGroup.页面参数, true, "秒级", "铂表面发射率 0.10–0.30 扫 9 点"),
        new("export.csv", "导出 CSV", StageId.粗算, ChainId.A单段解析,
            CmdGroup.导出, true, "即时", "导单段解的轴向温度分布"),
        new("line.runAll", "核算全线", StageId.粗算, ChainId.B分段解析,
            CmdGroup.页面参数, true, "即时", "逐段解析强度与铂重"),
        new("line.bestGrade", "为各段选最省牌号", StageId.粗算, ChainId.B分段解析,
            CmdGroup.页面参数, true, "即时", "逐段试各牌号，取最省的那个"),
        new("line.minWalls", "按强度取最小壁厚", StageId.粗算, ChainId.B分段解析,
            CmdGroup.页面参数, true, "即时", "把强度反算出的最小壁厚写回段表"),
        new("line.flanges", "核算法兰（分钟级）", StageId.粗算, ChainId.B分段法兰,
            CmdGroup.页面参数, true, "分钟级", "逐段一次耦合解，把法兰算进总铂"),

        // ── ③ 整线核算 ★ ──────────────────────────────────────────────
        new("core.runLine", "核算整线", StageId.整线核算, ChainId.C整线耦合,
            CmdGroup.页面参数, true, "分钟级，可取消",
            "按页面参数解一次耦合场，出判据表。**几何用的是和定案完全同一套构造器**"),
        new("geom.analyze", "分析几何变数", StageId.整线核算, ChainId.C整线耦合,
            CmdGroup.工具, true, "分钟级", "报各几何量对判据的斜率（只测不调）"),
        new("final.reproduce", "▶ 复现定案", StageId.定案档, ChainId.C整线耦合,
            CmdGroup.定案不读页面, false, "分钟级，可取消",
            "**完全不读页面控件**，直接按定案档解一次。用来排除「页面上某个控件被改过而自己没注意到」"),
        new("final.load", "载入定案", StageId.定案档, ChainId.无,
            CmdGroup.定案不读页面, false, "即时",
            "把 FinalDesign 的某一档灌进各控件。**已作废的档会在最前面自报失效**"),

        // ── ④ 定尺寸 ──────────────────────────────────────────────────
        new("core.autoThick", "自动定厚", StageId.定尺寸, ChainId.C定尺寸,
            CmdGroup.页面参数, true, "更久，可取消",
            "解析几何走 D8 定尺寸；.3dm 几何走逐级定厚。结束后附一份「形状体检」"),
        new("shape.search", "◇ 搜形状", StageId.定尺寸, ChainId.C形状搜索,
            CmdGroup.页面参数, true, "几十分钟",
            "**自动改盘径与舌宽**（舌长按装配算出来），逐个定尺寸并挑最轻的全过解。⚠ 只在解析几何模式可用"),
        new("scan.thickness", "② 厚度灵敏度", StageId.定尺寸, ChainId.C整线耦合,
            CmdGroup.页面参数, true, "很久",
            "10 个壁厚点各跑一次整线耦合解。**这是 C 链的工具，不是「分析」** —— 它的每一点都是权威解"),

        // ── ⑤ 交付 ────────────────────────────────────────────────────
        new("export.page3dm", "导出本页 3DM", StageId.交付, ChainId.无,
            CmdGroup.导出, true, "十几秒",
            "**整机**（三段管 + 四片法兰），几何与刚才求解的**完全一致**"),
        new("final.export3dm", "导出定案 3DM", StageId.定案档, ChainId.无,
            CmdGroup.定案不读页面, false, "十几秒",
            "整机几何 + 自校。**已声明失效的档一律拒绝出图**"),
        // ⚠ 这两个 ReadsPageControls **必须是 false**（2026-08-21 修）。
        //   它们读的是**左侧参数表**（DesignInputs），不是 ③ 页的控件，
        //   也不依赖任何解 —— 而 MainForm.SyncGates 正是按这个标志决定「锁 ⑤ 时禁哪些按钮」。
        //   标成 true 的后果：⑤ 没解锁时「保存」也被禁 ⇒
        //   **工程师调了半天参数存不下来，非得先解出一个收敛解才准存档**。
        //   存参数和「这一版几何算没算通」是两件毫不相干的事，
        //   而门禁的意义是拦住「拿不成立的解去出图」，不是拦住记事本。
        // 把当前的解写成 finaldesigns/*.fd.json。**读页面控件**（存的就是你手上这个解）
        // ⇒ 受适用性约束：AllOk + Fresh 才可用（见 LineDesignPage.CommandApplicable）。
        new("final.save", "另存为定案档", StageId.定案档, ChainId.无,
            CmdGroup.导出, true, "即时",
            "把当前这个**全判据通过**的解写成档案文件。五个判据记录值由程序填 —— "
            + "手抄它们是本项目最常见的错源，而抄错要跑 8 分钟 --selfcheck 才知道"),

        new("case.save", "保存", StageId.交付, ChainId.无,
            CmdGroup.工具, false, "即时", "把参数表存成 .json"),
        new("case.load", "读取", StageId.交付, ChainId.无,
            CmdGroup.工具, false, "即时", "从 .json 载入参数表。**载入后上一次的解即作废**"),

        // ── 使用说明 ──────────────────────────────────────────────────
        new("manual.openMd", "打开 Markdown 版", StageId.说明, ChainId.无,
            CmdGroup.工具, false, "即时",
            "在外部编辑器打开 docs\\APP使用说明书.md。⚠ 那份只留程序里讲不了的事，操作说明以本页为准"),
    };

    // ═══ 阶段 ═════════════════════════════════════════════════════════════
    //
    // 轨是线性的（左到右＝阅读顺序），但门禁是**依赖图**：
    //
    //     ① 闸门 ──┬──→ ② 快筛（末端，不解锁任何东西）
    //              └──→ ③ 整线核算 ★ ──→ ④ 定尺寸 ──→ ⑤ 交付
    //
    // ② **不是** ③ 的前提 —— 快筛只是可选的粗看。为了对称而给 ②→③ 加门，
    // 会把「工程师明明可以直接算整线」拦下来，那种门第二天就会被要求关掉。
    public static readonly StageSpec[] Stages =
    {
        // ── 定案档：**不带编号**，因为它不是阶段轨的一格。
        //
        // 这几条命令早就带着同一个标记 CmdGroup.定案不读页面 —— 它们**不读页面控件**，
        // 也因此不受阶段门禁。而 ①→⑤ 说的是「你手上这个设计走到哪一步」。
        // 两根轴正交，此前被混在 ③ 的同一条工具条上（用户 2026-08-23 要求拆出来）。
        //
        // 放在**最前面**而不是最后：载入/复现会**灌页面控件**，是给 ③ 喂起点的。
        // 摆在 ⑤ 之后等于把入口放在出口。
        //
        //   [定案档] 载入/复现 ─→ ① ② ③ ④ ⑤ ─→ [定案档] 另存/出图
        //
        // GateToUnlockNext = null ⇒ 下一格（①）不受它约束，本页自己也永不上锁。
        new(StageId.定案档, 0, "定案档",
            "档里的数与你手上这一版是**两回事**。「载入」把档灌进页面当起点；"
            + "「▶ 复现定案」完全不读页面控件，用来排除「页面被改过而不自知」。",
            new[] { ChainId.C整线耦合 },
            GateToUnlockNext: null,
            new[] { "final.reproduce", "final.load", "final.save", "final.export3dm" },
            Array.Empty<string>()),

        new(StageId.先决条件, 1, "① 先决条件（能造 · 能升温）",
            "先决条件先答：能不能造、能不能用是一个 yes/no 闸门。**不过闸就到此为止 —— 不谈铂重、不谈优点。**",
            new[] { ChainId.D升温闸 },
            // 解锁 ③：几何可造（⑤⑥）+ 升温快筛不判死。
            // 这两条都是**闭式**的，毫秒可得 ⇒ 不必先跑分钟级的整线解就能开门。
            new GateSpec(
                new[] { LineResult.Key.FreeTab, LineResult.Key.DiscCover },
                RequireConverged: false, RequireAllOk: false, RequireFresh: false,
                LockedTitle: "③ 整线核算 —— 还没解锁",
                LockedWhy: "先决条件还没答。业主 2026-08-17：「前提还是要能造能用，省铂金是在这个前提下讨论的」"
                         + " —— 不过闸就到此为止，不谈铂重。"),
            new[] { "gate.ramp" },
            new[] { "D 闸门" }),

        new(StageId.粗算, 2, "② 粗算（解析 · 不可交付）",
            "**本区是解析粗算，不解温度场；判据与交付数一律以「③ 整线核算」页为准。**"
            + "　⚠ 本页段表与 ③ 页段表**互不同步**。",
            new[] { ChainId.A单段解析, ChainId.A单段扫描, ChainId.B分段解析, ChainId.B分段法兰 },
            GateToUnlockNext: null,   // 末端节点，不解锁任何东西
            new[] { "calc.segment", "sweep.insul", "sweep.eps", "export.csv",
                    "line.runAll", "line.bestGrade", "line.minWalls", "line.flanges" },
            new[] { "A·B 快筛" }),

        new(StageId.整线核算, 3, "③ 整线核算 ★",
            "**唯一可交付的一条链。** 判据表由 LineRunner.Judge 给 —— 全程只有这一个来源。",
            new[] { ChainId.C整线耦合 },
            // 解锁 ④ 的门是「解得出来且收敛」，**不是「判据全过」**。
            // ④ 的用途就是把不过的判据调过来 —— 用 AllOk 当门会把正常用法整个锁死。
            // 但起点必须能解：否则 Sizer 的几十分钟全烧在一个坏几何上。
            new GateSpec(
                Array.Empty<string>(),
                RequireConverged: true, RequireAllOk: false, RequireFresh: true,
                LockedTitle: "④ 定尺寸 —— 还没解锁",
                LockedWhy: "定尺寸器每轮都要跑一次整线解，起点必须是一个**解得出来且收敛**的构型。"),
            new[] { "core.runLine", "geom.analyze" },
            new[] { "C 整线", "A·B·C 共用" }),

        new(StageId.定尺寸, 4, "④ 定尺寸 / 搜形状",
            "④ 的输入就是 ③ 的解，**不是新的输入** —— 本页只有命令与 ③ 结果的只读摘要。",
            new[] { ChainId.C定尺寸, ChainId.C形状搜索 },
            // 解锁 ⑤ 的门是 AllOk。理由现成：ExportBlockedReason 已确立
            // 「交付件不能是一个自己声明不成立的设计」。
            new GateSpec(
                Array.Empty<string>(),
                RequireConverged: true, RequireAllOk: true, RequireFresh: true,
                LockedTitle: "⑤ 交付 —— 还没解锁",
                LockedWhy: "交付件不能是一个自己声明不成立的设计。"),
            new[] { "core.autoThick", "shape.search", "scan.thickness" },
            Array.Empty<string>()),

        new(StageId.交付, 5, "⑤ 交付（出图 / 存档）",
            "出图前请核对输出里的**逐件质量对账**（差应在 ±1 % 内，对不上就别出图）。",
            new[] { ChainId.无 },
            GateToUnlockNext: null,
            new[] { "export.page3dm", "case.save", "case.load" },
            Array.Empty<string>()),

        // 没有门（上一格 GateToUnlockNext 为 null）⇒ 永远解锁。F1 直达。
        new(StageId.说明, 6, "使用说明",
            "图按定案档实时生成 —— 换一档，图跟着变。",
            new[] { ChainId.无 },
            GateToUnlockNext: null,
            new[] { "manual.openMd" },
            Array.Empty<string>()),
    };

    // ═══ 参数分区 ═════════════════════════════════════════════════════════
    //
    // 「在参数表里改了它，到底对哪条链有效？」—— 这是「不知道自己在算什么」的
    // 另一半病因。整线链上有一批参数**根本不看参数表**：有的被 ③ 页控件接管，
    // 有的被 LineCase 的硬编码默认值吃掉。
    //
    // ⚠ 标注，不隐藏。用 PropertyGrid.BrowsableAttributes 过滤会让**没标注到的**
    //   属性静默消失，而「看不见又在起作用」正是本项目反复栽的那一类。
    //   宁可看得见但明写着「无效」。
    public static readonly ParamScope[] Params =
    {
        new("1 A·B 粗算 — 工艺", new[] { ChainId.A单段解析, ChainId.B分段解析 }, ""),
        new("2 A·B·C 共用 — 电气",
            new[] { ChainId.A单段解析, ChainId.B分段解析, ChainId.C整线耦合 }, ""),
        new("3 A·B·C 共用 — 保温与表面",
            new[] { ChainId.A单段解析, ChainId.B分段解析, ChainId.C整线耦合 }, ""),
        new("4 A·B·C 共用 — 玻璃物性",
            new[] { ChainId.A单段解析, ChainId.B分段解析, ChainId.C整线耦合 }, ""),
        new("5 C 整线 — 管几何", new[] { ChainId.C整线耦合 }, ""),
        new("6 C 整线 — 法兰边界", new[] { ChainId.C整线耦合 }, ""),
        new("8 ✗ 被页面/定案档接管（改了对整线链没用）", new[] { ChainId.A单段解析, ChainId.B分段解析 },
            "「③ 整线核算」页的同名控件"),
        new("9 ✗ 对整线链无效", new[] { ChainId.A单段解析 },
            "LineRunner 强制取值（整线链的壁厚由 LineCase.WallMm 定）"),
        new("7 数值", new[] { ChainId.A单段解析, ChainId.C整线耦合 }, ""),
    };

    /// <param name="CmdId">该点的那个命令（Flow.Commands 里的 Id）。空串 = 没有下一步可指。</param>
    /// <param name="Why">为什么是它 —— 一句话，直接显示给用户。</param>
    public sealed record NextStep(string CmdId, string Why);

    /// <summary>
    /// 「我现在该点哪个按钮」。
    ///
    /// ★ 用户 2026-08-20 的原话是「工程师根本不知道自己目前在算什么」。
    ///   阶段横幅、门禁说明、状态面板都在回答「**你在哪**」，
    ///   却没有一处回答「**往哪走**」—— 这条补的就是后者。
    ///
    /// ⚠⚠ **只读现成状态，绝不产生新判据**（铁律一）。
    ///   下面每一个分支的依据都来自 <see cref="FlowState"/> 已有的字段
    ///   （Last / Fresh / AllOk / Converged），一个数都不重算。
    ///   一旦这里自己算点什么，判据就有了第二个来源 —— 那是本项目连错五次的根源。
    ///
    /// ⚠ 故意**不做成「下一步」按钮直接执行**（2026-08-22 与用户议定）：
    ///   ④ 会改你的输入、③ 要跑几十秒、⑤ 会写文件。一个按钮包住这些，
    ///   等于把「我知道我在做什么」从工程师手里拿走 —— 而那正是要治的病。
    ///   这里只指路，动作仍由人下。
    /// </summary>
    public static NextStep? Next(FlowState st)
    {
        // 正在算的时候不催 —— 状态面板那一行已经在说「正在算：…」
        if (st.Running is not null) return null;

        if (st.Last is null)
            return new("core.runLine", "还没解过 —— 先解一次整线，才谈得上判据与出图");

        if (!st.Last.Ok || !st.Last.Converged)
            return new("core.runLine", "上次没收敛 ⇒ 那组数一个都不可引用 —— 重解一次");

        // ⚠ 这一条必须排在 AllOk 前面：参数改过之后，AllOk 说的是**上一组参数**的事，
        //   拿它去指路等于让人照着过期结论走下一步。
        if (!st.Fresh)
            return new("core.runLine", "参数在上次求解之后又动过了 —— 回 ③ 按现在这组重解");

        if (!st.Last.AllOk)
        {
            // ★★ 分清「厚度能救」与「厚度救不了」（用户 2026-08-22 提醒补上搜形状）。
            //
            //   判据 ⑤ 舌片自由段、⑥ 圆盘盖得住管孔 是**纯几何**的：
            //   它们只取决于盘径 / 舌长 / 舌端半宽，**与板厚无关** ——
            //   「自动定厚」把厚度调到上限也过不了，只会白跑几分钟然后说还是不过。
            //   这两条不过时该动的是形状 ⇒ 指向「◇ 搜形状」。
            //
            //   反过来，②′/②″/③/管J 是热-电耦合量，厚度正是它们的旋钮 ⇒ 指向「自动定厚」。
            //
            //   ⚠ 判据名一律走 LineResult.Key 常量，不写字符串字面量 ——
            //     判据改名时编译期就断，而不是这条指路悄悄指错方向。
            bool geomBlocked = st.Last.Checks.Any(c =>
                (c.Kind is CheckKind.HardSafety or CheckKind.Target)
                && (!c.Ok || c.Undetermined)
                && (c.Name.StartsWith(LineResult.Key.FreeTab, StringComparison.Ordinal)
                 || c.Name.StartsWith(LineResult.Key.DiscCover, StringComparison.Ordinal)));

            if (geomBlocked)
                return new("shape.search",
                    "卡住的是**几何**判据（舌片自由段 / 圆盘盖住管孔）—— "
                    + "厚度调不动它们，要改盘径与舌宽：点「◇ 搜形状」（几十分钟）");

            return new("core.autoThick", "判据没全过，卡的是热-电量 —— 用「自动定厚」把厚度调到过");
        }

        return new("export.page3dm", "判据全过且是当前参数的解 —— 可以出图了");
    }

    // ═══ 查询 ═════════════════════════════════════════════════════════════

    public static CommandSpec Cmd(string id)
        => Commands.FirstOrDefault(c => c.Id == id)
           ?? throw new ArgumentException($"Flow 里没有登记命令「{id}」");

    public static StageSpec Stage(StageId s)
        => Stages.First(x => x.Id == s);

    public static ChainSpec Chain(ChainId c)
        => Chains.First(x => x.Id == c);

    /// <summary>某条链归哪个阶段（第一个包含它的阶段）。</summary>
    public static StageSpec Of(ChainId c)
        => Stages.First(s => s.Chains.Contains(c));

    /// <summary>阶段轨上的下一格；已是最后一格则返回 null。</summary>
    public static StageSpec? Next(StageId s)
    {
        var cur = Stage(s);
        return Stages.OrderBy(x => x.Order).FirstOrDefault(x => x.Order > cur.Order);
    }

    /// <summary>
    /// 自检：Id 唯一、CommandIds 指得到、每条命令都被恰好一个阶段收录。
    /// 构造期就跑，坏了当场炸 —— 这份表一旦与界面漂开，四个消费者会一起错。
    /// </summary>
    public static void SelfTest()
    {
        var dupCmd = Commands.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1);
        if (dupCmd is not null) throw new InvalidOperationException($"Flow：命令 Id 重复「{dupCmd.Key}」");

        var dupStage = Stages.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1);
        if (dupStage is not null) throw new InvalidOperationException($"Flow：阶段重复「{dupStage.Key}」");

        foreach (var s in Stages)
            foreach (var id in s.CommandIds)
                if (Commands.All(c => c.Id != id))
                    throw new InvalidOperationException($"Flow：阶段「{s.Title}」引用了不存在的命令「{id}」");

        foreach (var c in Commands)
        {
            int n = Stages.Count(s => s.CommandIds.Contains(c.Id));
            if (n != 1)
                throw new InvalidOperationException(
                    $"Flow：命令「{c.Id}」被 {n} 个阶段收录（应恰好 1 个）");
            if (Stage(c.Stage).CommandIds.Contains(c.Id) == false)
                throw new InvalidOperationException(
                    $"Flow：命令「{c.Id}」自称属于「{c.Stage}」，但那个阶段没收录它");
        }

        foreach (var c in Chains.Where(x => x.Id != ChainId.无))
            if (Stages.All(s => !s.Chains.Contains(c.Id)))
                throw new InvalidOperationException($"Flow：链「{c.Name}」没有归属阶段");
    }
}

// ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// ③④⑤ 共享的运行时状态 —— 「**解**」的单一来源，与「**判据**」的单一来源
/// （LineRunner.Judge）一一对应。
///
/// 为什么要有它：④「定尺寸」与 ⑤「交付」的输入就是 ③ 的那个解。在此之前
/// 这个解只活在 LineDesignPage 的私有字段里，于是任何想用它的人只能把按钮
/// 塞回 ③ 的工具条 —— 那正是今天「十个按钮一横排」的病因。
/// </summary>
public sealed class FlowState
{
    /// <summary>最近一次 C 链解。null = 还没解过，或上一次的解已作废。</summary>
    public LineResult? Last;

    /// <summary>解出 <see cref="Last"/> 时的参数快照，用来判「新鲜」。</summary>
    public object? SolvedSnap;

    /// <summary>当前页面参数的快照。与 <see cref="SolvedSnap"/> 相等即「新鲜」。</summary>
    public object? CurrentSnap;

    /// <summary>⑤⑥ 几何闭式判据，随参数毫秒刷新。</summary>
    public ConstraintOut[] GeomScreen = Array.Empty<ConstraintOut>();

    /// <summary>D 链升温快筛。⚠ 这是快筛，① 的交付判定由 <see cref="Last"/> 给。</summary>
    public ConstraintOut? RampScreen;

    /// <summary>正在跑的链（null = 空闲）。状态面板据此显示「正在算…」。</summary>
    public ChainId? Running;

    /// <summary>进度文字，取自各页已有的 Progress&lt;string&gt;。</summary>
    public string RunningNote = "";

    /// <summary>
    /// 被越关进入的阶段。**按会话，不持久化** —— 能被存进文件的例外，
    /// 三个月后就变成了没人记得来由的默认值。
    /// </summary>
    public readonly HashSet<StageId> Bypassed = new();

    /// <summary>结果是否新鲜（参数没动过）。没解过时为 false。</summary>
    public bool Fresh => Last is not null && SolvedSnap is not null
                      && Equals(SolvedSnap, CurrentSnap);

    public event Action? Changed;
    public void Notify() => Changed?.Invoke();

    /// <summary>
    /// 标记「现在正在跑哪条链」。**开跑时设、结束时清（放 finally）**。
    ///
    /// ★ 2026-08-21：这两个字段本来就声明着、StagePanel 也早就在读
    ///   （「正在算：… （再点那个按钮 = 取消）」），**但从来没有人赋值** ——
    ///   又一个「接了一半」的空壳（同族：FillChecks 无调用者、_pAx 从没画过）。
    ///   后果：④ 页点「自动定厚」要跑三分多钟，而那一页**没有进度条也没有状态标签**
    ///   （_prog/_status 都长在 ③ 上）⇒ 界面一动不动，看着像卡死了。
    ///
    /// 状态面板在右上角、**切到哪一页都看得见** ⇒ 接上它，
    /// 就不必给每一页各配一套进度条（那又会变成「同一件事多处表达」）。
    ///
    /// 它同时是**互斥的单一来源**：MainForm.SyncGates 据此把所有会起算的命令禁掉，
    /// 免得三个分钟级求解同时开跑（④ 上的三个按钮此前正是可以同时点的）。
    /// </summary>
    public void SetRunning(ChainId? chain, string note = "")
    {
        Running = chain;
        RunningNote = note;
        Notify();
    }

    /// <summary>只更新进度文字（不改「在跑哪条链」）。供 Progress&lt;string&gt; 直接接。</summary>
    public void SetRunningNote(string note)
    {
        if (Running is null) return;      // 已经结束了就别再刷，免得残留一行假进度
        RunningNote = note;
        Notify();
    }

    /// <summary>上一次的解已作废（读取了新方案、切换了几何来源等）。</summary>
    public void Invalidate()
    {
        Last = null; SolvedSnap = null;
        Notify();
    }
}

/// <summary>
/// 门禁引擎。
///
/// ⚠⚠ **只读判据，不产生任何判定。** 这里一行 `if (actual > limit)` 都不许出现 ——
///   判据只有一个来源（LineRunner.Judge / GeometryScreen.Judge），门禁只是**读**它。
///   一旦门禁自己算一遍，就是铁律三点名的「散在三处」的第三处。
/// </summary>
public static class Gate
{
    public readonly record struct Status(
        StageId Stage,
        bool Unlocked,
        bool Bypassed,
        string Why,
        string How,
        ConstraintOut? Blocking);

    /// <summary>某个阶段能不能进。<paramref name="stage"/> 是**要进入的**那一格。</summary>
    public static Status Evaluate(StageId stage, FlowState st)
    {
        var target = Flow.Stage(stage);

        // 第一格永远开着 —— 总得有个入口。
        var prev = Flow.Stages.OrderByDescending(x => x.Order)
                              .FirstOrDefault(x => x.Order < target.Order);
        if (prev is null)
            return new Status(stage, true, false, "", "", null);

        var gate = prev.GateToUnlockNext;
        if (gate is null)
            return new Status(stage, true, false, "", "", null);

        bool bypassed = st.Bypassed.Contains(stage);

        // ── 逐条读判据（不重算）
        foreach (string key in gate.RequiredChecks)
        {
            var c = FindCheck(key, st);
            // 判不出来一律不算过（铁律二：「无法判定」一律不算通过）
            if (c is null || c.Undetermined || !c.Ok)
                return new Status(stage, bypassed, bypassed,
                                  gate.LockedWhy, HowFrom(c), c);
        }

        if (gate.RequireConverged && st.Last is not { Ok: true, Converged: true })
            return new Status(stage, bypassed, bypassed, gate.LockedWhy,
                              "回到「③ 整线核算」点「核算整线」；若它提示「残差仍在收缩 —— 是慢不是发散」，"
                              + "答应把轮数上限提上去。", null);

        if (gate.RequireAllOk && st.Last?.AllOk != true)
        {
            var worst = Worst(st);
            return new Status(stage, bypassed, bypassed, gate.LockedWhy, HowFrom(worst), worst);
        }

        if (gate.RequireFresh && !st.Fresh)
            return new Status(stage, bypassed, bypassed, gate.LockedWhy,
                              "参数在上次求解之后又动过了 —— 回到「③ 整线核算」重解一次。", null);

        return new Status(stage, true, bypassed, "", "", null);
    }

    /// <summary>门禁只管「读页面控件」的命令；「定案」那四个不受约束。</summary>
    public static bool Blocks(CommandSpec cmd, FlowState st)
        => cmd.ReadsPageControls && !Evaluate(cmd.Stage, st).Unlocked;

    // ── 内部 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 先找权威解里的那条，找不到再退到闭式快筛。
    /// **权威优先**：③ 跑出真解之后，Judge 的结论覆盖快筛 —— 少了这条，
    /// 快筛就成了一张能绕过权威判据的通行证。
    /// </summary>
    private static ConstraintOut? FindCheck(string key, FlowState st)
        => st.Last?.Find(key)
           ?? st.GeomScreen.FirstOrDefault(c => c.Name.StartsWith(key, StringComparison.Ordinal))
           ?? (st.RampScreen?.Name.StartsWith(key, StringComparison.Ordinal) == true
                   ? st.RampScreen : null);

    /// <summary>没过的里面最该先解决的那条 —— 与判据表的排序规则一致：硬安全线优先，其次超限最狠。</summary>
    private static ConstraintOut? Worst(FlowState st)
        => st.Last?.Checks
            .Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target
                     && (!c.Ok || c.Undetermined))
            .OrderBy(c => c.Kind == CheckKind.HardSafety ? 0 : 1)
            .ThenByDescending(c => Math.Abs(c.Limit) < 1e-9
                                       ? Math.Abs(c.Actual)
                                       : Math.Abs(c.Actual - c.Limit) / Math.Abs(c.Limit))
            .FirstOrDefault();

    /// <summary>
    /// 「怎么解锁」的文字**直接引用判据自己的 Note**（NextAction 写的【下一步】），
    /// 零新文案 —— 门禁不该有自己的一套说法。
    /// </summary>
    private static string HowFrom(ConstraintOut? c)
    {
        if (c is null) return "";
        string note = c.Note ?? "";
        int i = note.IndexOf("【下一步】", StringComparison.Ordinal);
        return i >= 0 ? note[i..] : note;
    }
}
