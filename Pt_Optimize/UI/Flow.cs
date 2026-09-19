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
//   TextFmt 是 internal，于是测试只能反射进去（tests/UiWiring 里 §17 与 §18 那两处
//   `Assembly.GetType("PtOptimize.UI.TextFmt")`）——
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
    /// <summary>自动定厚（Solver.Solve / FlangeAutoSizer.SolveByLevel）—— C 的外层</summary>
    C定尺寸,
    /// <summary>形状搜索（Solver.Solve × N 个形状）—— C 的最外层</summary>
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
/// <summary>
/// 阶段轨。★★★★★ 2026-09-02 由七格收成四格（原：设计记录/①先决条件/②粗算/③整线核算/
/// ④定尺寸/⑤交付/说明）。依据是**实测**，不是审美 —— 见 <see cref="Flow.Stages"/> 上的说明。
///
/// 主线两格，直接对应用户说的模型：**输入 → 计算 → 储存结果报告与图档**。
/// 「参考工具」与「使用说明」不带编号、永远进得去。
/// </summary>
/// <summary>
/// ★★★★★ 阶段轨（R21，用户 2026-09-08：「APP 的操作流程：UI 或 3DM 输入 → 法兰优化 → 计算结果与出图」）：
///   ① 输入（<see cref="输入"/>）→ ② 法兰优化（<see cref="整线核算"/>，成员名沿用旧名以免全仓改动）→ ③ 结果与出图（<see cref="交付"/>）；
///   参考工具／使用说明不带编号。
/// </summary>
public enum StageId { 输入, 整线核算, 交付, 参考工具, 说明 }

/// <summary>
/// 命令的三组分法 —— 沿用说明书里已经教给用户的那套（ManualPage §2.3）。
/// 那里写着：「「设计记录」两个字打头的那几个**不读页面上的控件**……中间三个**读页面控件**」。
/// </summary>
public enum CmdGroup
{
    /// <summary>「设计记录」打头：只认 DesignSpec，页面上改什么都影响不了它们 ⇒ **不受阶段门禁**</summary>
    设计记录不读页面,
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
    bool Deliverable)
{
    /// <summary>
    /// ★★★ 去掉链代号的名字：<c>"C′ 定尺寸"</c> → <c>"定尺寸"</c>（2026-09-03）。
    ///
    /// 链代号 A/B/C/D 与 ′ ″ 是**给写的人分类用的**，工程师看不懂 ——
    /// 与判据代号 ②′②″③ 是同一条规矩（用户 2026-08-30：「工程师看不懂」），
    /// 只是当时没人扫到状态面板这一行。
    ///
    /// ⚠ <see cref="Name"/> 本身不动：命令行、HANDOVER、测试都按它认链。剥壳只在显示层。
    /// </summary>
    public string PlainName
    {
        get
        {
            string n = Name.Trim();
            if (n.Length > 0 && n[0] is >= 'A' and <= 'D')
            {
                int k = 1;
                if (k < n.Length && (n[k] == '′' || n[k] == '″')) k++;
                if (k < n.Length && n[k] == ' ') n = n[(k + 1)..];
            }
            return n.Replace("★", "").Trim();
        }
    }
}

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
    string LockedWhy,
    /// <summary>
    /// ★★★ 还要求**判据经过网格无关复核**（2026-08-30）。
    /// 只加在 ④→⑤ 那道门上：出图是唯一「把数交出去」的动作，
    /// 而在此之前那道门只问「解出来了吗」，不问「这个数可不可信」。
    /// </summary>
    bool RequireMeshVerified = false);

/// <param name="Id">稳定锚点（如 "core.runLine"）。测试与说明书按它引用，
/// 于是**改中文名不会打破任何东西**。</param>
/// <param name="Text">界面按钮上的字 —— 说明书按钮表用的是**同一个字符串**。</param>
/// <param name="ReadsPageControls">false ⇒ 不受阶段门禁（「设计记录」那四个）。</param>
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
    /// <param name="ParamCategoryPrefixes">
    /// 本格**用得上**的参数类别（按 <c>Contains</c> 匹配 <c>[Category]</c> 的名字）。
    /// 切到这一格时，命中的类别展开、其余折叠。
    ///
    /// ⚠ **折叠，不是隐藏**：类别标题还在，点一下就开。用 PropertyGrid 的
    ///   BrowsableAttributes 过滤会让**没标注到的**属性静默消失，而「看不见又在起作用」
    ///   正是本项目反复栽的那一类。
    /// </param>
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
            "Solver.Solve", "更久", false),
        new(ChainId.C形状搜索, "C″ 形状搜索", "连盘径与舌宽一起搜，挑最轻的全过解",
            "Solver.Solve × N", "几十分钟", false),
        new(ChainId.D升温闸, "D 升温闸", "空管能不能在期限内升到目标温度（闭式快筛）",
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
        new("gate.ramp", "升温可达性趋势", StageId.参考工具, ChainId.D升温闸,
            CmdGroup.页面参数, true, "毫秒",
            "5 档保温 × 4 档壁厚，闭式算升温时间与电流密度。**这是快筛，升温的交付判定在「整线核算」页给**"),

        // ── ② 快筛（解析·不可交付）──────────────────────────────────────
        new("calc.segment", "计算 (F5)", StageId.参考工具, ChainId.A单段解析,
            CmdGroup.页面参数, true, "秒级",
            "按左侧参数表解一次单段。**不含法兰** —— 报告里没有法兰的任何一项"),
        new("sweep.insul", "扫描：保温厚度", StageId.参考工具, ChainId.A单段扫描,
            CmdGroup.页面参数, true, "秒级", "内层保温 0–50 mm 扫 11 点"),
        // ⚠ 这里曾有第三个扫描「扫描：法兰厚度」，2026-08-20 删除：
        //   它传的量名 "flangeTf" 在 SegmentSolver.Sweep 里不存在，11 行全是同一个基准解。
        //   A 链结构上不含法兰 ⇒ 补 case 只能是假的。权威答案是 ④ 的「② 厚度灵敏度」。
        new("sweep.eps", "扫描：铂发射率", StageId.参考工具, ChainId.A单段扫描,
            CmdGroup.页面参数, true, "秒级", "铂表面发射率 0.10–0.30 扫 9 点"),
        new("export.csv", "导出 CSV", StageId.参考工具, ChainId.A单段解析,
            CmdGroup.导出, true, "即时", "导单段解的轴向温度分布"),
        new("line.runAll", "核算全线", StageId.参考工具, ChainId.B分段解析,
            CmdGroup.页面参数, true, "即时", "逐段解析强度与铂重"),
        new("line.bestGrade", "为各段选最省牌号", StageId.参考工具, ChainId.B分段解析,
            CmdGroup.页面参数, true, "即时", "逐段试各牌号，取最省的那个"),
        new("line.minWalls", "按强度取最小壁厚", StageId.参考工具, ChainId.B分段解析,
            CmdGroup.页面参数, true, "即时", "把强度反算出的最小壁厚写回段表"),
        new("line.flanges", "核算法兰（分钟级）", StageId.参考工具, ChainId.B分段法兰,
            CmdGroup.页面参数, true, "分钟级", "逐段一次耦合解，把法兰算进总铂"),

        // ── ③ 整线核算 ★ ──────────────────────────────────────────────
        // ★★★★★ 2026-09-02：它**不再只是解一次**（用户：「全整到核算整线」）。
        //   一路走到能出图为止：解 → 判据没过就自己调厚度 →（几何卡住就搜形状）→ 加密复算。
        //   ⚠ 耗时标注必须跟着改：原来写「分钟级」，而现在最长可能一两个小时 ——
        //     一个说「分钟级」却跑两小时的按钮，比不写还坏。
        new("core.runLine", "核算整线", StageId.整线核算, ChainId.C整线耦合,
            CmdGroup.页面参数, true, "十几分钟～两小时，全程有进度，随时可取消",
            "**一路算到能出图为止**：解一次 → 判据没过就自己调厚度（必要时连形状一起搜）"
            + " → 加密复算到数不再变。几何用的是和设计记录完全同一套构造器"),
        new("geom.analyze", "分析几何变数", StageId.输入, ChainId.C整线耦合,
            CmdGroup.工具, true, "分钟级", "报各几何量对判据的斜率（只测不调）"),
        new("geom.toanalytic", "◈ 图纸几何 → 参数", StageId.输入, ChainId.无,
            CmdGroup.工具, true, "瞬时",
            "把 .3dm 反推出来的几何（盘径／舌长／舌半宽／管壁／板厚）**交给解析路**，并切到解析模式。" +
            "⇒ 「◇ 搜形状」随之可用 —— 那是全程唯一能**改形状**的东西，而 .3dm 路改不了形状" +
            "（它的旋钮只有各级厚度）。⚠ 交接之后几何不再跟图纸绑定；图纸本身没被改动，切回去即可"),
        new("geom.export3dm", "导出可回读 3DM", StageId.整线核算, ChainId.无,
            CmdGroup.工具, true, "十几秒",
            "把本页解析几何写成**单图层多级台阶**的 .3dm —— 一张 APP 自己读得回来的图。" +
            "现有的出图是多图层，读取端要单图层，于是「出图 → 去 Rhino 改 → 读回来核算」这条路是断的。" +
            "写完立刻回读校验"),
        new("final.load", "载入设计记录", StageId.参考工具, ChainId.无,
            CmdGroup.设计记录不读页面, false, "即时",
            "把 DesignSpec 的某一档灌进各控件。**已作废的档会在最前面自报失效**"),

        // ★★★ 网格无关复核（2026-08-30 补）。
        //   在此之前它**只有命令行有**（`--solve --verifymesh`），界面上一次都调不到 ——
        //   `MeshVerify.Run` 在 UI 里的调用次数是 0。而 ⑤ 交付的门也不要求它。
        //   ⇒ 工程师可以拿一个导航网格上的数直接出图，而那个数实测能差 1.8 K。
        new("core.verifyMesh", "◆ 加密复算（算到数不再变）", StageId.整线核算, ChainId.C整线耦合,
            CmdGroup.页面参数, true, "10～40 分钟，可取消",
            "把网格一档档加密，直到判据不再变。**判据可不可信由它说了算** —— 出图前必须过这一关"),

        // ★★★★★ R26（2026-09-09）：加密复算发现导航网格上过、**细网格上不过**时的出口。
        //   起因：VerifyMeshAsync 只要「网格无关」（Converged）就把 _last 换成细网格那份结果，
        //   不问它 AllOk —— 于是 Flow.Next 落进「判据没全过」那一支，原来一律指「自动定厚」，
        //   而 D8 的默认调用只在导航网格上求根（FineMm = 0）⇒ 又全过 ⇒ 又要求加密复算 ⇒
        //   又不过 ⇒ 指纹交替、白跑 8 步（见本项目 HANDOVER R26 行，Ø56 盘那份实例）。
        //   ⇒ 直接在**判据所在的那张细网格**上重新求根：旋钮只增不减，从第一遍（导航网格）
        //   的解出发 —— 与「搜形状」精算胜出形状用的是同一条路（Solver.Solve 两遍）。
        new("core.fineResolve", "◆ 细网格重解（加密不过后）", StageId.整线核算, ChainId.C整线耦合,
            CmdGroup.页面参数, true, "十几分钟到数小时，可取消",
            "加密复算发现判据在细网格上不过（导航网格上是过的）—— 不必回导航网格重来一遍："
            + "直接在这张细网格上重新求根，旋钮只增不减。解完仍要回「◆ 加密复算」再验一次才可出图。"),

        // ── ④ 定尺寸 ──────────────────────────────────────────────────
        // ★★★ 2026-09-04：这两个是**手动分步**，不是并列的第三、第四条路。
        //   「核算整线」内部已经把它们串起来了（RunPipelineAsync：解 → 定厚 → 搜形状
        //   → 加密复算），而且结构上本来就是外层/内层 —— **搜形状的每一个点内部
        //   都在跑自动定厚**（Solver.Solve）。
        //   界面上三颗并排摆着，工程师会以为要自己挑一个 ⇒ 提示里明说它们的关系。
        new("core.autoThick", "自动定厚（手动分步）", StageId.整线核算, ChainId.C定尺寸,
            CmdGroup.页面参数, true, "数分钟～半小时，可取消",
            "★ **「核算整线」已经包含这一步** —— 这里单独点，是想只调厚度、先看一眼结果。"
            + "解析几何走 D8 定尺寸；.3dm 几何走逐级定厚。结束后附一份「形状体检」"),
        new("shape.search", "◇ 搜形状（手动分步）", StageId.整线核算, ChainId.C形状搜索,
            CmdGroup.页面参数, true, "几十分钟",
            "★ **「核算整线」已经包含这一步**，而且它每试一个形状都会在里面重跑一遍定厚。"
            + "这里单独点，是想只在形状这一维上看结果。"
            + 
            "**自动改盘径与舌宽**（舌长按装配算出来），逐个定尺寸并挑最轻的全过解。⚠ 只在解析几何模式可用"),
        new("scan.thickness", "厚度灵敏度扫描", StageId.整线核算, ChainId.C整线耦合,
            CmdGroup.页面参数, true, "很久",
            "10 个壁厚点各跑一次整线耦合解。**这是 C 链的工具，不是「分析」** —— 它的每一点都是权威解"),

        // ── ⑤ 交付 ────────────────────────────────────────────────────
        new("report.install", "导出安装报告", StageId.交付, ChainId.无,
            CmdGroup.导出, true, "即时",
            "把解出的整线连同**配套清单**（每片的铜排规格、保温覆盖、焊脚）、判据结论、待现场确认项写成一份安装报告（.md）；" +
            "内容与「③ 结果与出图 ▸ 安装报告」页签完全一样，同一个来源"),
        new("export.page3dm", "导出本页 3DM", StageId.交付, ChainId.无,
            CmdGroup.导出, true, "十几秒",
            "**整机**（三段管 + 四片法兰），几何与刚才求解的**完全一致**"),
        new("final.export3dm", "导出设计记录 3DM", StageId.参考工具, ChainId.无,
            CmdGroup.设计记录不读页面, false, "十几秒",
            "整机几何 + 自校。**已声明失效的档一律拒绝出图**"),
        // ⚠ 这两个 ReadsPageControls **必须是 false**（2026-08-21 修）。
        //   它们读的是**左侧参数表**（DesignInputs），不是 ③ 页的控件，
        //   也不依赖任何解 —— 而 MainForm.SyncGates 正是按这个标志决定「锁 ⑤ 时禁哪些按钮」。
        //   标成 true 的后果：⑤ 没解锁时「保存」也被禁 ⇒
        //   **工程师调了半天参数存不下来，非得先解出一个收敛解才准存档**。
        //   存参数和「这一版几何算没算通」是两件毫不相干的事，
        //   而门禁的意义是拦住「拿不成立的解去出图」，不是拦住记事本。
        // 把当前的解写成 finaldesigns/*.fd.json。**读页面控件**（存的就是你手上这个解）
        // ⚠ 适用性只剩 Fresh（2026-09-02 用户拍板：过没过由工程师判断，程序不替他否决）。
        //   ★ 这条提示原来写「全判据**通过**的解」+「手抄记录值／8 分钟全档自检」——
        //     前半句与现在的行为**相反**（超标也存得下，只是会先把问题列给你看），
        //     后半句是给开发者的话。两处都是抓图抓到的（2026-09-03）。
        new("final.save", "另存为设计记录", StageId.交付, ChainId.无,
            CmdGroup.导出, true, "即时",
            "把当前这个解连同判据值一起存成档案。**超标也存得下** —— "
            + "存之前会把超标的项列给你看，由你决定存不存；存下来的档会记着你担了这个风险。"),

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
    // ★★★★★ 2026-09-02 重排：七格 → 四格。依据是**实测**，不是审美。
    //
    //   Flow.Next（蓝色指示的唯一来源）在全部分支里只会指向 7 条命令，而
    //   `gate.ramp`（原 ① 页）与原 ② 粗算那 8 条**一条都不在里面** ——
    //   工程师一路点蓝色走完全程，那两页从头到尾不会被访问，
    //   而阶段轨却把它们编成 ①②、摆在最前面。**导航在说谎。**
    //   原 ④ 定尺寸页更直接：它没有任何自己的内容，三个按钮的所有者都是别的页。
    //
    //   新轨直接对应用户 2026-09-02 说的模型：**输入 → 计算 → 储存结果报告与图档**。
    //
    //     ① 整线核算 ★ ──→ ② 交付           （主线，两格）
    //     参考工具 / 使用说明                 （不带编号，永远进得去）
    //
    //   ⚠ 功能一个没删：原 ①② 的九条命令、以及「设计记录」那一组校正命令，
    //     全部搬进「参考工具」。降级的是**编号与位置**，不是能力。
    public static readonly StageSpec[] Stages =
    {
        // ★★★★★ R21（用户 2026-09-08）：三步主线 ① 输入 → ② 法兰优化 → ③ 结果与出图。
        //   ① 输入：左边参数表 + 管／法兰形状／厚度等控件，或读一张 .3dm 图纸（分析 → 交给解析路）。
        //   没有门：输入永远进得去。
        new(StageId.输入, 1, "① 输入",
            "填左边的参数表和本页控件（管、法兰形状、解法），或选一张法兰 .3dm 图纸（各片同一张图，厚度各自解）。"
            + "段数在「铂金管加热段数」框，每段管长在段表里；板厚、舌片厚、舌保温、环倍率由 APP 解出，不用填。"
            + "填好到「② 法兰优化」点「核算整线」。",
            new[] { ChainId.无 },
            GateToUnlockNext: null,
            new[] { "geom.analyze", "geom.toanalytic" },
            new[] { ParamCat.判据限值与窗口, ParamCat.电气, ParamCat.保温与表面, ParamCat.玻璃物性,
                    ParamCat.管几何, ParamCat.法兰与铜排, ParamCat.数值 }),

        new(StageId.整线核算, 2, "② 法兰优化",
            "点「核算整线」：一路算到能出图为止（解一次 → 按 J=10 定截面与自动定厚 → 必要时搜形状 → 加密复算）。"
            + "全程有进度、随时可取消；分步按钮收在「手动分步」里，平时不用碰。",
            new[] { ChainId.C整线耦合, ChainId.C定尺寸, ChainId.C形状搜索 },
            // ★★★ 进「交付」的门只留一条：**有一个当前参数的收敛解**。
            //
            //   用户 2026-09-02：「计算结果是如何就如何，超标就显示提醒，
            //   最终让工程师判断合格与否（风险由工程师判断）；若判断可承担风险，
            //   工程师就可储存计算结果与出图」。
            //   ⇒ 「过没过」「验没验过」**不再是拦**，改成动作当下弹确认
            //     （见 LineDesignPage 的另存与出图）。拿掉的是拦，**不是指路** ——
            //     Flow.Next 照旧建议「先加密复算再出图」。
            //
            //   ⚠ 仍然拦「没解出来 / 参数动过了」：那不是风险判断，是**错** ——
            //     没有解就无从交付；参数动过之后交出去的是**上一组参数**的东西。
            new GateSpec(
                Array.Empty<string>(),
                RequireConverged: true, RequireAllOk: false, RequireFresh: true,
                LockedTitle: "③ 结果与出图 —— 还没解锁",
                LockedWhy: "交付要有一个**当前参数的、解得出来的**解。"
                         + "过没过、准不准由你判断（存或出图时会把问题列给你看）—— "
                         + "但「没解出来」和「参数动过了」不是风险，是对不上。"),
            // ★ R26（2026-09-09）：core.fineResolve 排在 core.verifyMesh 之后 —— 它是
            //   加密复算不过之后的下一步，物理顺序与命令顺序（本页工具条实际按钮顺序）一致。
            new[] { "core.runLine", "core.autoThick", "shape.search", "core.verifyMesh",
                    "core.fineResolve", "geom.export3dm", "scan.thickness" },
            // ⚠ 这里是**类别名的片段**，按 Contains 匹配（不是 StartsWith）——
            //   真实类别名带编号前缀（「5 C 整线 — 管几何」），写「C 整线」用前缀匹配永远不中。
            //   2026-08-20 建这个字段时就写错了，而它**一直没有消费者**，所以错了两周没人知道。
            // ⚠ 2026-09-03 参数表类别改成人话（原来带链代号 A·B·C / C 整线，工程师看不懂）
            //   ⇒ 这份名单必须同步。ParamFoldTests 盯着「每条前缀都要匹配到真类别」。
            new[] { ParamCat.判据限值与窗口, ParamCat.电气, ParamCat.保温与表面, ParamCat.玻璃物性,
                    ParamCat.管几何, ParamCat.法兰与铜排, ParamCat.数值 }),

        new(StageId.交付, 3, "③ 结果与出图",
            "判据表、场图、各片厚度都在这里。把这一版交出去：出图纸、存成设计记录。有问题会先列给你看，由你决定存不存。",
            new[] { ChainId.无 },
            GateToUnlockNext: null,
            new[] { "export.page3dm", "report.install", "final.save", "case.save", "case.load" },
            Array.Empty<string>()),

        // ── 不带编号的两格：永远进得去（前一格 GateToUnlockNext 为 null）。
        new(StageId.参考工具, 4, "参考工具",
            "**这一页不是设计的起点。** 设计从「① 整线核算」页开始 —— 那里有两个入口：" +
            "填 UI 参数，或读一张 .3dm 图纸。厚度、保温、环倍率这些由 APP 自己解出来，不从档里抄。" +
            Environment.NewLine +
            "本页是**解析粗看与校正**，不可交付：粗看用来快速比趋势；" +
            "「载入设计记录」把已归档的设计灌进 ① 页，之后点「核算整线」得到的就是记录里的数（走查逐项钉着）；改设计仍从 ① 页那两个入口开始。" +
            "判据与交付数一律以「① 整线核算」页为准。",
            new[] { ChainId.D升温闸, ChainId.A单段解析, ChainId.A单段扫描,
                    ChainId.B分段解析, ChainId.B分段法兰, ChainId.C整线耦合 },
            GateToUnlockNext: null,
            new[] { "gate.ramp",
                    "calc.segment", "sweep.insul", "sweep.eps", "export.csv",
                    "line.runAll", "line.bestGrade", "line.minWalls", "line.flanges",
                    "final.load", "final.export3dm" },
            new[] { ParamCat.工艺条件, ParamCat.电气,
                    ParamCat.保温与表面, ParamCat.玻璃物性 }),

        new(StageId.说明, 5, "使用说明",
            "图按设计记录实时生成 —— 换一档，图跟着变。",
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
        // ★ U 路（2026-09-18，Opus 5）：温差预算两项与「终验时量可行窗口」的开关 —— 只对整线链有效（判据只在整线链上判）。
        new(ParamCat.判据限值与窗口, new[] { ChainId.C整线耦合 }, ""),
        new(ParamCat.工艺条件, new[] { ChainId.A单段解析, ChainId.B分段解析 }, ""),
        new(ParamCat.电气,
            new[] { ChainId.A单段解析, ChainId.B分段解析, ChainId.C整线耦合 }, ""),
        new(ParamCat.保温与表面,
            new[] { ChainId.A单段解析, ChainId.B分段解析, ChainId.C整线耦合 }, ""),
        new(ParamCat.玻璃物性,
            new[] { ChainId.A单段解析, ChainId.B分段解析, ChainId.C整线耦合 }, ""),
        new(ParamCat.管几何, new[] { ChainId.C整线耦合 }, ""),
        new(ParamCat.法兰与铜排, new[] { ChainId.C整线耦合 }, ""),
        new(ParamCat.页面接管, new[] { ChainId.A单段解析, ChainId.B分段解析 },
            "「整线核算」页的同名控件"),
        new(ParamCat.程序算出, new[] { ChainId.A单段解析 },
            "整线核算自己定（管壁由「整线核算」页上的壁厚控件决定）"),
        new(ParamCat.数值, new[] { ChainId.A单段解析, ChainId.C整线耦合 }, ""),
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
    /// <param name="inapplicable">此刻**用不了**的命令 Id（几何来源不对等）。
    /// 指路不许指到它们身上 —— 指着一个灰按钮说「点这个」比不指更糟。</param>
    public static NextStep? Next(FlowState st, Func<string, bool>? applicable = null)
    {
        // 正在算的时候不催 —— 状态面板那一行已经在说「正在算：…」
        if (st.Running is not null) return null;

        // ★ .3dm 且还没分析：先分析，再解。顺序不能颠倒 ——
        //   不分析直接解，⑤⑥ 会是「无法判定」，那一分多钟等于白跑。
        if (st.GeomAnalysisPending)
            return new("geom.analyze",
                "本页是 **Rhino .3dm 模式**，图纸还没反推成几何变数 —— "
                + "先点它，「舌片自由段」与「圆盘盖得住管孔」才判得了（否则解完仍是「无法判定」，白跑一次分钟级的解）");

        // ★★★★★ R19（用户 2026-09-08）：**3DM 路要能进第 ② 步搜形状**。
        //   3DM 是输入的一种；优化本来就要改形状（② 按 J=10 定 r₁/t₁、r₂/t₂ = 搜形状），而 .3dm 模式改不了形状。
        //   ⇒ 图纸分析过、且照图纸解过一次（那是「改前」基准）之后，下一步就是把图纸交给解析路 ——
        //   **不管那一次过没过、收没收敛**：不收敛照旧指「重解」会原地打转（截面/⑥ 到头时正是这样死的）。
        //   ⚠ 只有「解的就是这张图（新鲜）」或「那次根本没解出来」才交；参数动过而上次是好解 ⇒ 先按现在这张图重解。
        if (st.Last is not null && applicable is not null && applicable("geom.toanalytic")
            && (st.Fresh || !(st.Last.Ok && st.Last.Converged)))
            return new("geom.toanalytic",
                  "图纸照原样解过一次了（那是「改前」的基准）。本模式改不了**形状**，而优化要改的正是形状"
                + "（按 J=10 定 r₁/t₁、r₂/t₂ 与盘径、舌宽）—— "
                + "⇒ 点「◈ 图纸几何 → 参数」：把图纸反推出来的几何（盘径／舌长／舌半宽／管壁／基板厚／各级台阶）"
                + "**逐级**交给解析路。那条路能改形状，「舌片自由段」与「圆盘盖得住管孔」也就判得了。"
                + "　想留在图纸上也行：那就只能调厚度，或回 Rhino 改图。");

        if (st.Last is null)
            return new("core.runLine",
                "还没解过。它会自己走完：解一次 → 判据没过就调旋钮（需要时搜形状）→ "
                + "加密复算到数不再变 → 细网格上不过就重解 → 可以出图");

        if (!st.Last.Ok || !st.Last.Converged)
            return new("core.runLine", "上次没收敛 ⇒ 那组数一个都不可引用 —— 重解一次");

        // ⚠ 这一条必须排在 AllOk 前面：参数改过之后，AllOk 说的是**上一组参数**的事，
        //   拿它去指路等于让人照着过期结论走下一步。
        if (!st.Fresh)
            return new("core.runLine", "参数在上次求解之后又动过了 —— 回「整线核算」按现在这组重解");

        if (!st.Last.AllOk)
        {
            // ★★★★★ R26（2026-09-09）：**必须排在这一支的最前面**。
            //
            //   `st.Last` 在这里可能已经不是导航网格那份解了 —— `VerifyMeshAsync` 只要
            //   「网格无关」（MeshVerified）就把它换成细网格那份结果，不问 AllOk（见
            //   FlowState.MeshVerified 与 LineDesignPage.VerifyMeshAsync 的说明）。
            //   所以「MeshVerified && VerifiedFresh 但 !AllOk」= 加密复算验出「导航网格上
            //   过、细网格上不过」。这时**不能**落到下面「自动定厚」那条 ——
            //   D8 的默认调用只在导航网格上求根，会重新算出「全过」，指路又回去要求
            //   加密复算，加密复算又验出不过 ⇒ 两个按钮**交替指、永远走不到出图**
            //   （核算整线走满 8 步会因此白跑，见 HANDOVER R26 行 Ø56 盘那份实例）。
            //   ⇒ 直接指向「在细网格上重新求根」，跳过导航网格这一趟。
            if (st.MeshVerified && st.VerifiedFresh)
                return new("core.fineResolve",
                      "加密复算到位的数不过（导航网格上过、细网格上不过）⇒ 在细网格上重新求根：从当前解出发，旋钮只增不减");

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

            // ⚠ .3dm 模式下「◇ 搜形状」**用不了**（形状由图纸给定，不是可搜索的自由度）。
            //   而 ⑤⑥ 在那个模式下必然「无法判定」⇒ geomBlocked 恒真 ⇒
            //   不加这一层，界面会稳定地指着一个**灰按钮**说「点这个」。
            //   实测（Pt_Heater3.3dm 走一遍）：下一步 → ◇搜形状，适用=False 可点=False。
            //   ★★ 2026-08-25：这条死路有出口了。用户点破了根因 ——
            //     「3DM 只读几何数据（画网格的依据），为何不能带入计算？」
            //     能。反推一直都在做（PlateShapeAnalyzer 印的那几个数就是），只是没交出去。
            //     交给解析路之后形状就能改，⑤⑥ 也就判得了。
            //   ⚠ 但「◈ 图纸几何 → 参数」要求**已经分析过**（否则没有形状可交）——
            //     没分析就先指分析，**绝不指一个灰按钮**（正是本段上一条注释栽过的坑）。
            if (geomBlocked && applicable is not null && !applicable("shape.search"))
            {
                if (applicable("geom.toanalytic"))
                    return new("geom.toanalytic",
                          "几何判据在本模式下**判不了**（形状由 .3dm 给定，「舌片自由段」与「圆盘盖得住管孔」无从判起）—— "
                        + "⇒ 点「◈ 图纸几何 → 参数」：把图纸反推出来的几何（盘径／舌长／舌半宽／"
                        + "管壁／板厚）交给**解析路**。那条路能改**形状**，那两条也就判得了。"
                        // 2026-09-14 Opus 5（复审）：这条实测是换热偶读数基准之前、按旧判法的两条量的 ⇒ 写明，别让人以为是现在卡交付的判据
                        + "　实测（旧判法）：盘Ø120 即便板厚顶到工艺下界，圆盘区最高温与法兰增量温降仍差一个数量级 —— "
                        + "**卡住的往往是形状，不是厚度**。"
                        + "　想留在图纸上也行：那就只能调厚度，改完回「整线核算」重解，或回 Rhino 改图。");
                return new("geom.analyze",
                      "几何判据在本模式下**判不了**（形状由 .3dm 给定）—— "
                    + "先点「分析几何变数」把图纸的几何读出来；读出来之后才谈得上"
                    + "把它交给解析路（那条路能改形状）。");
            }

            if (geomBlocked)
                return new("shape.search",
                    "卡住的是**几何**判据（舌片自由段 / 圆盘盖住管孔）—— 厚度调不动它们，"
                    + "要改盘径与舌宽。点「核算整线」，它会自己去搜（几十分钟，随时可取消）");

            // ★★★★★ 指自动定厚之前，先问它**接不接这张图**（2026-08-25 `--follow3dm` 抓到）。
            //
            //   `.3dm` 模式下「逐级定厚」要求分析出来**≥2 级台阶**。
            //   Pt_Heater1.3dm 分析出来是**等厚板（1 级）** ⇒ 点下去当场被拒、
            //   判据表逐字不变，而这里继续指它 ⇒ **死循环**。
            //   更糟的是拒绝讯息写「请先点『分析几何变数』」，而用户第一步就点过了 ——
            //   把人指回一个他已经按过、再按也不会有结果的按钮。
            //
            //   ⚠ 等厚板并非没有厚度旋钮：`.3dm` 模式下本页的厚度控件就是**厚度标度 k**。
            //     所以出路是「手改 k 再重解」或「回 Rhino 给圆盘分级」，**不是再点一次分析**。
            //   ⚠ 这里只给说明、不指按钮：能按的那个是页面上的数值框，不是命令按钮。
            //   ★ 2026-08-25 这条死路有出口了：新增「◈ 图纸几何 → 参数」——
            //     把反推出来的几何交给解析路（那条路能改形状）。用户当天点破了根因：
            //     「3DM 只读几何数据，为何不能带入计算？」—— 能，只是一直没接上。
            // ★★★ 2026-09-03：这一位的含义**变了** —— 现在只表示「还没分析」。
            //   原来它是「等厚板（<2 级）」，于是等厚板被整个拒在门外（F 撞死在这）。
            //   查清后放宽：SolveByLevel 是两层的，1 级只让内层失效，外层照常能调
            //   ⇒ 等厚板照样能点「自动定厚」，指路不再把人推走。
            //   ⚠ 「还没分析」本来就有专门的一位（GeomAnalysisPending）在更前面处理，
            //     所以走到这里时它几乎不会成立；留着它是因为**判据与拒绝必须同一个来源**
            //     （拒绝写在 RunAsync，指路读这一位）。
            if (st.SizerNoLevels)
                return new("geom.analyze",
                      // 2026-09-14 Opus 5（复审）：原列「法兰增量温降／圆盘区最高温」—— 那两条已降为参考量、不卡交付，真正卡交付的热侧／冷侧两条反而没提。
                      //   判据名走 Criteria.Plain(Key)：改名时这里跟着变，不再手抄。
                      $"卡的是热-电量（{Criteria.Plain(LineResult.Key.NetFlux)}／{Criteria.Plain(LineResult.Key.HotOverTc)}／"
                    + $"{Criteria.Plain(LineResult.Key.ColdUnderTc)}／管电流密度），"
                    + "而厚度正是它们的旋钮 —— 但图纸**还没反推**成厚度场，无从算起。"
                    + "⇒ 先点「分析几何变数」。");
            // ★★★★★ R48 M（2026-09-18，Fable 5.1）：**判不了 ≠ 不可行**。上一次求解器在某一点没解到收敛（判不了）时，
            //   不许把人推去「搜形状」—— 判不了不是形状的事（实测同一点重跑会收敛：r48_U 2026-09-18 不收敛点诊断）。
            //   下一步是把这一点解到收敛：重解（没收敛时它会问要不要加轮数上限）或细化网格重算。
            //   ⚠ 必须排在 SizerProvedInfeasible 之前：判不了与旋钮到顶在求解器里互斥（Solver.Rounds），这里也不许让「到顶」那句抢先。
            if (st.SolverUndetermined)
                return new("core.runLine",
                      "**上一次求解在某一点没解到收敛 —— 判不了，不是不可行**"
                    + (st.SolverUndeterminedWhy.Length > 0 ? "：" + st.SolverUndeterminedWhy.Replace("**", "") : "")
                    + "　⇒ 这一点没解到收敛：可加轮数上限／细化网格重算 —— 点「核算整线」重解（没收敛时它会问你要不要加轮数上限），"
                    + "或点「◆ 加密复算」在细网格上重算。不要因此去改形状：判不了不是形状的事。");
            // ★★★★★ 指「自动定厚」之前，先问它**上一次是不是已经宣告不可行**（2026-09-03）。
            //   见 FlowState.SizerProvedInfeasible 的说明：走查实测连指 3 次 = 死循环。
            //   厚度这条路已经走到头 ⇒ 换下一根杠杆，而不是把人推回同一个按钮。
            if (st.SizerProvedInfeasible)
            {
                if (applicable is not null && applicable("shape.search"))
                    return new("shape.search",
                          "**厚度这条路已经走到头** —— 上一次「自动定厚」把旋钮抬到上界仍不过，"
                        + "那是**法兰侧旋钮到顶的证明**（不是整个设计不可行），再点一次会得到同一句话。"
                        + "⇒ 下一根杠杆是**形状**：点「◇ 搜形状」，它会改盘径与舌宽"
                        + "（导热截面 ∝ 宽×厚，缩小盘径 / 收窄舌片才动得了法兰抽走的热）。"
                        + "　几十分钟，随时可取消。");
                if (applicable is not null && applicable("geom.toanalytic"))
                    return new("geom.toanalytic",
                          "**厚度这条路已经走到头** —— 上一次「自动定厚」把旋钮抬到上界仍不过。"
                        + "本页是 .3dm 模式，改不了形状 ⇒ 点「◈ 图纸几何 → 参数」"
                        + "把几何交给解析路，那条路才搜得了盘径与舌宽。");
                // 两根自动杠杆都用尽 ⇒ **老实说没有自动的下一步了**，别再指一个必然无效的按钮。
                return new("core.runLine",
                      "**自动的招用完了** —— 厚度顶到上界仍不过，形状也搜过了。"
                    + "接下来要你改输入：控温点、管径/管壁、铂牌号，或放宽这一段的工况。"
                    + "改完点「核算整线」按新的这组重解。"
                    + "　（也可以就这样交付 —— 超标项会列给你，风险由你判断。）");
            }
            return new("core.autoThick", "判据没全过，卡的是热-电量（厚度正是它们的旋钮）—— "
                + "点「核算整线」，它会自己往下调厚度，不用你决定调哪个、调多少");
        }

        // ★★★ 全过 ≠ 可信。判据是在**导航网格**上判的，先验一次它准不准（2026-08-30）。
        if (!(st.MeshVerified && st.VerifiedFresh))
            return new("core.verifyMesh",
                  "判据全过且是当前参数的解 —— 但这些数是在**导航网格**上算的，还没验过准不准。"
                + "点「核算整线」，它会自己接着加密复算（把网格一档档加密，直到这个数不再变）。"
                // 2026-09-14 Opus 5（复审）：历史实测是旧判法的数 ⇒ 标明（同 LineDesignPage 那一句）
                + "　⚠ 值得等：0.6 档那个设计，粗网格算出「法兰增量温降（旧判法）7.7 K」（旧判法限值 10，看着很宽），"
                + "加密到位是 **9.5 K** —— 差 1.8 K，而这个差足以把「过」变成「不过」。");

        return new("export.page3dm", "判据全过、是当前参数的解、而且已经加密复算到数不再变 —— 可以出图了");
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

    /// <summary>
    /// ★★★ **这一次的解经过网格无关复核了吗**（2026-08-30 补）。
    ///
    /// ══ 为什么非有它不可
    ///
    /// 在此之前，⑤ 交付的门只要求「收敛 + 全过 + 新鲜」——**没有一条要求判据可信**。
    /// 而判据是在**导航网格（2 mm）**上判的，实测那个数能差多少：
    /// <code>
    ///   0.6 档「法兰增量温降」  粗网格 7.685 K   →   加密到位 9.453 K   差 1.777 K
    /// </code>
    /// 限值是 10 K ⇒ **「全过」这个判定本身可能是假的**。
    /// 于是工程师可以：点核算整线 → 收敛✓全过✓新鲜✓ → ⑤ 解锁 → 出图，
    /// 而那张图背后的数没有任何人验过。
    ///
    /// ⚠ 命令行早就有这道复核（`--solve --verifymesh`），**界面上一次都调不到** ——
    ///   `MeshVerify.Run` 在 UI 里的调用次数是 **0**。
    ///   对话里跑得漂亮、工程师点按钮却碰不到，正是本项目最怕的那种落差。
    /// </summary>
    public bool MeshVerified;

    /// <summary>复核那一刻的参数快照 —— 与 <see cref="CurrentSnap"/> 不等就作废。</summary>
    public object? VerifiedSnap;

    /// <summary>复核的结论原话（供状态面板与提示直接印）。空 = 没复核过。</summary>
    public string VerifyNote = "";

    /// <summary>
    /// 复核**还算不算数**：复核那一刻的参数与现在一致才算。
    /// ⚠ 与 <see cref="Fresh"/> 同一个道理 —— 改完参数还挂着上一次的复核结论，
    ///   那是**假绿灯**，比没有复核更坏（它会让人以为验过了）。
    /// </summary>
    public bool VerifiedFresh =>
        VerifiedSnap is not null && CurrentSnap is not null && VerifiedSnap.Equals(CurrentSnap);

    /// <summary>
    /// `.3dm` 模式、图纸已选、但**还没「分析几何变数」**。
    ///
    /// ★ 为什么要单独立一位（2026-08-23 用户指出）：不分析也能点「核算整线」，
    ///   但那样跑完一次分钟级的解，⑤⑥ 仍是「无法判定」——
    ///   **白跑一分多钟才发现该先点分析**。指路必须先把这一步说出来。
    /// </summary>
    public bool GeomAnalysisPending;

    /// <summary>
    /// `.3dm` 模式下，**逐级定厚有没有可调的级**（分析出来 ≥2 级才有）。
    ///
    /// ★ 为什么要单独立一位（2026-08-25 `--follow3dm` 用 Pt_Heater1.3dm 抓到）：
    ///   那张图分析出来是**等厚板（1 级）**，于是「自动定厚」**当场拒绝**，
    ///   判据表逐字不变 —— 而指路只看「过没过」，继续指同一个按钮 ⇒ **死循环**。
    ///   更糟的是拒绝讯息写着「请先点『分析几何变数』」，而用户**第一步就点过了**：
    ///   把人指回一个他已经按过、再按也不会有结果的按钮。
    ///
    ///   ⚠ 等厚板并非没有厚度旋钮：`.3dm` 模式下本页的厚度控件就是**厚度标度 k**
    ///     （无量纲，图纸整体 ×k）。所以出路是「手改 k」或「回 Rhino 分级」，
    ///     不是「再点一次分析」。
    /// </summary>
    /// ⚠ **写成「例外才为真」而不是「正常才为真」**（2026-08-25，UiWiring §28 当场抓到）：
    ///   头一版写的是 `SizerHasLevels`，而 `bool` 新建默认 **false** ⇒
    ///   一个还没跑过 SyncAnalysisPending 的 FlowState 会被判成「没有可调的级」，
    ///   连**解析路**都不再指「自动定厚」。**新状态位默认落在危险的那一侧**，
    ///   这正是本项目反复栽的形态。倒过来写之后，默认 false = 正常。
    public bool SizerNoLevels;

    /// <summary>
    /// ★★★★★ **定尺寸器已经证明「这组输入不可行」**（2026-09-03，跑 F 时抓到）。
    ///
    /// 实况（Pt_Heater1.3dm，图纸几何交给解析路之后）：
    /// <code>
    ///   自动定厚 → 「片0 舌保温 抬到上界 20.000 仍不过『法兰增量温降』⇒ 这组输入不可行」
    ///   判据表逐字不变 → 指路仍指「自动定厚」 → 再点 → 同一句话 → …
    ///   走查器实测：**连指 3 次**。
    /// </code>
    ///
    /// 病灶与 <see cref="SizerNoLevels"/> **完全同形**：引擎自己说了「不可行」，
    /// 而指路只看「判据过没过」⇒ 继续指同一个按钮。那一处堵的是「等厚板」这个入口，
    /// 这一处堵的是「旋钮顶到上界」这个入口 —— 同一个坑的两个洞，当时只补了一个。
    ///
    /// ⚠ <see cref="PtOptimize.Core.SolverResult.HitBound"/> 的语义（2026-09-08 收窄）：
    ///   **法兰侧旋钮顶到上界 = 再点一次会得到同一句话**，不是「搜索没搜到」。两者不能混：
    ///   没搜到该再搜，顶到上界再搜多少次都是同一句话。
    ///   ⚠⚠ 但它**不是**「这组输入不可行」的证明 —— 求解器手上只有法兰侧九根旋钮，
    ///     盘径与舌半宽不在它手里。**一个出口只能对它自己有的旋钮下结论**：
    ///     HitBound 之后正确的下一步是「◇ 搜形状」（本方法正是这么指的），不是收摊。
    ///
    /// ⚠ 同样写成「例外才为真」（见上一条的教训）：默认 false = 正常。
    /// </summary>
    public bool SizerProvedInfeasible;

    /// <summary>
    /// ★★★★★ R48 M（2026-09-18，Fable 5.1）：**求解器上一次判不了**（<see cref="PtOptimize.Core.SolverResult.Undetermined"/>）——
    /// 某一点场解解不出来／外层耦合未收敛／格点裕度小于认证误差走到上界仍判不了。与 <see cref="SizerProvedInfeasible"/> 互斥：
    /// 判不了的下一步是把这一点解到收敛（加轮数上限／细化网格重算），**不是**搜形状。默认 false = 正常（例外才为真）。
    /// </summary>
    public bool SolverUndetermined;

    /// <summary>判不了的那一句（哪片哪根旋钮哪个值处，裕度 x 小于认证误差 y）。</summary>
    public string SolverUndeterminedWhy = "";

    /// <summary>进度文字，取自各页已有的 Progress&lt;string&gt;。</summary>
    public string RunningNote = "";

    /// <summary>
    /// 进度 %（0–100）。**−1 = 说不出百分比**（走马灯）。
    ///
    /// 为什么放在这里而不是各页自己的进度条上：2026-08-21 定过「不给每页各配一套」——
    /// 而那条决定当时只落实了一半：状态面板拿到了**文字**，却始终没有条。
    /// 于是 ④「定尺寸/搜形状」上一根条都没有（它的按钮是从 ③ 借来的，进度条没借），
    /// 而搜形状恰恰是全程最长的一条（几十分钟）。
    /// ⇒ 数放这儿，**面板画一根**：一份状态、一根条、每页都看得见。
    /// </summary>
    public int RunningPct = -1;

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
        RunningPct = -1;              // 新的一段活，进度从「说不出」重新开始
        Notify();
    }

    /// <summary>
    /// 只更新进度（不改「在跑哪条链」）。供 Progress&lt;string&gt; 直接接。
    /// <paramref name="pct"/> 传 −1 表示这一段说不出百分比（面板画走马灯）。
    /// </summary>
    public void SetRunningNote(string note, int pct = -1)
    {
        if (Running is null) return;      // 已经结束了就别再刷，免得残留一行假进度
        RunningNote = note;
        RunningPct = pct < 0 ? -1 : System.Math.Clamp(pct, 0, 100);
        Notify();
    }

    /// <summary>
    /// **参数动过 ⇒ 这条链从头走一遍**（2026-08-24 用户要求：
    /// 「APP 使用期间只要参数有任何更动（APP 没有在计算），计算都需重头开始，
    ///   链路指示也需重头更新，直至存档出图」）。
    ///
    /// 作废的是一切「已经过了」的**凭据**，而不是数字本身：
    ///   · <see cref="SolvedSnap"/> = null ⇒ Fresh 变 false ⇒ ④⑤ 两道门关上，
    ///     指路回到「回「整线核算」按现在这组重解」；
    ///   · <see cref="Bypassed"/> 清空 —— ★ 这一条此前**漏了**：
    ///     越关是在**旧参数**上批的，参数一动它就不该再算数。
    ///     不清它，工程师改完参数还站在一个「当初批准进来的」页面上，
    ///     而批准的理由已经不存在了。
    ///
    /// 判据表（<see cref="Last"/>）**故意留着**：数字留给人对照「改之前是多少」，
    /// 但它旁边会写明是上一组参数的。要连表一起清的是 <see cref="Invalidate"/>。
    /// </summary>
    public void RestartChain()
    {
        SolvedSnap = null;
        Bypassed.Clear();
        Notify();
    }

    /// <summary>上一次的解已作废（读取了新方案、切换了几何来源等）—— 连判据表一起清。</summary>
    public void Invalidate()
    {
        Last = null;
        RestartChain();
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
                              "回到「整线核算」点「核算整线」；若它提示「残差仍在收缩 —— 是慢不是发散」，"
                              + "答应把轮数上限提上去。", null);

        if (gate.RequireAllOk && st.Last?.AllOk != true)
        {
            var worst = Worst(st);
            return new Status(stage, bypassed, bypassed, gate.LockedWhy, HowFrom(worst), worst);
        }

        if (gate.RequireFresh && !st.Fresh)
            return new Status(stage, bypassed, bypassed, gate.LockedWhy,
                              "参数在上次求解之后又动过了 —— 回到「整线核算」重解一次。", null);

        // ★★★ 判据可不可信（2026-08-30）。排在最后：前面几条不成立时，
        //   先说更根本的原因（还没解出来就谈不上「这个数准不准」）。
        if (gate.RequireMeshVerified && !(st.MeshVerified && st.VerifiedFresh))
            return new Status(stage, bypassed, bypassed, gate.LockedWhy,
                st.MeshVerified
                    ? "参数在加密复算之后又动过了 —— 回「整线核算」页再点一次「◆ 加密复算（算到数不再变）」。"
                    : "判据是在**导航网格**上判的，还没验过它准不准。"
                      + "回「整线核算」页点「◆ 加密复算（算到数不再变）」——"
                      + "它会把网格一档档加密，直到判据不再变（实测 10–40 分钟，随时可取消）。"
                      + "　⚠ 不验就出图的风险是实打实的：0.6 档那个设计，"
                      + "粗网格算出「法兰增量温降（旧判法）7.7 K」（旧判法限值 10，看着很宽），"
                      + "加密到位是 **9.5 K** —— 差 1.8 K，而这个差足以把「过」变成「不过」。", null);

        return new Status(stage, true, bypassed, "", "", null);
    }

    /// <summary>一条命令此刻为什么不能点。<see cref="None"/> = 能点。</summary>
    public enum Block
    {
        /// <summary>能点</summary>
        None,
        /// <summary>有链在跑 —— 会起算的命令一律让路，免得几个分钟级求解互相覆盖结果</summary>
        Busy,
        /// <summary>当前页面状态下这条命令无从谈起（如解析模式下的「分析几何变数」）</summary>
        NotApplicable,
        /// <summary>所在阶段还没解锁</summary>
        Gate,
    }

    /// <summary>
    /// **一条命令能不能点，只由这里回答。**
    ///
    /// ⚠ 2026-08-23 之前这个方法只算门禁一项，而 MainForm.SyncGates 另外**内联**算了
    ///   门禁 × 互斥 × 适用性三项 —— 于是它成了一个**答案已经过时的死代码**：
    ///   长得像「问某个命令能不能点」的正门，谁调它谁得到和界面不一样的答案。
    ///   只因当时没人调，这件事才看不见。
    ///   ⇒ 现在三个因素收进这一处，SyncGates 只负责把结果画出来。
    ///
    /// <paramref name="applicable"/> 由命令的**归属页**提供（页面状态，Flow 不知道）。
    /// 不给则一律当适用。
    /// </summary>
    public static Block Blocks(CommandSpec cmd, FlowState st, Func<string, bool>? applicable = null)
    {
        // 「不算东西」的命令（保存/读取/出图/载入设计记录）不受互斥牵连 ——
        // 门禁的意义是拦住「拿不成立的解去出图」，不是拦住记事本。
        if (st.Running is not null && cmd.Chain != ChainId.无) return Block.Busy;
        if (applicable is not null && !applicable(cmd.Id)) return Block.NotApplicable;
        if (cmd.ReadsPageControls && !Evaluate(cmd.Stage, st).Unlocked) return Block.Gate;
        return Block.None;
    }

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
