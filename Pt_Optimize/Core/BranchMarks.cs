namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **「走到过」的痕迹** —— 让难以构造的分支自己留下可断言的一句话（2026-09-08）。
///
/// ══ 为什么要有这个
///
/// 2026-09-07 一天里加了三处修复，**没有一处被实测走过**：
/// <code>
///   熔化「只抬最热那一片」   自检 E 段 6 轮正常收敛，没触发
///   二分 mid 判不了 ⇒ 中止   0.8 档那 72 次场解全收敛，没踩到
///   抬前/上界 判不了          同上
/// </code>
/// 而 <c>dotnet test</c> 674/674 绿、自检通过，**对这三处一个字都没说** ——
/// 它们是「**在空集上恒对**」，与「门在空集上恒过」是同一个病，只是从测试挪到了代码。
///
/// ══ 手段：断言「走到了」，不是断言「结果好」
///
/// 造一个会熔/会不收敛的算例去验**结果**很贵也很难；
/// 但只要那条分支被**执行**过，它就能往轨迹里写一句话，门断言那句话出现即可。
/// **不必让它真熔，只要让那条分支被走到。**
///
/// ══ 为什么是常数不是字面
///
/// 门引的是**符号**（<c>BranchMarks.MeltRaiseHottest</c>），不是字面串 ——
/// 改文案不会让门静默失效，改符号名编译就断。这与本仓「不许钉措辞」是一致的：
/// 钉的是「这条分支必须留痕」这个**约定**，不是那句话怎么写。
///
/// ⚠ 这些字会出现在工程师的输出框里，所以必须是**人话**（见 HANDOVER「界面不许出现代号」）。
///
/// ⚠ 登记规矩：本类每多一个常数，<c>BranchMarksAreCoveredTests</c> 就要求
///   ① Core 里真的有人发它　② 有测试真的断言过它。少一样就红 —— 防的正是
///   「加了痕迹却没人看」这一族（「造好了没接线」）。
/// </summary>
public static class BranchMarks
{
    /// <summary>FlangeAutoSizer.Solve：熔化 ⇒ 只抬**最热那一片**的厚度（不是整片乘）。</summary>
    public const string MeltRaiseHottest = "★ 走到了「熔化 ⇒ 只抬最热那一片」";

    /// <summary>FlangeAutoSizer.Solve：厚度到顶仍熔 ⇒ **交棒**给增宽/搜形状（不是判无解）。</summary>
    public const string MeltHandOff = "★ 走到了「厚度到顶 ⇒ 交棒给增宽」";

    /// <summary>Solver.RaiseUntil：**抬之前**那一点就判不了（场解不收敛/不存在）。</summary>
    public const string UndeterminedBefore = "★ 走到了「抬前判不了」";
    /// <summary>R23（2026-09-10）：切口旋钮落地后舌片厚按 I/(J·最窄有效宽) 闭式重定 —— 这条分支真的被走到。</summary>
    public const string TongueResized = "★ 舌片厚随切口重定";

    /// <summary>Solver.RaiseUntil：抬到**上界**那一点判不了 ⇒ 上界存疑。</summary>
    public const string UndeterminedAtHi = "★ 走到了「上界判不了」";

    /// <summary>Solver.RaiseUntil：**二分中点**判不了 ⇒ 中止二分（不许当「不过」往上推）。</summary>
    public const string UndeterminedBisect = "★ 走到了「二分中点判不了 ⇒ 中止」";

    /// <summary>
    /// ★ R48 M（2026-09-18，Fable 5.1）：Solver.WalkConservative（RaiseUntil 的格点判决）：格点上这条判据的裕度 ≥ 0 但**小于认证误差**
    /// ⇒ 判不了那么细 ⇒ 往保守方向再走一格重判。后面跟着「片j 旋钮 值 处「判据」裕度 x 小于认证误差 y」。
    /// </summary>
    public const string GridWalkConservative = "★ 走到了「格点裕度小于认证误差 ⇒ 往保守方向再走一格」";

    /// <summary>
    /// ★ R48 M（2026-09-18，Fable 5.1）：Solver.WalkConservative／RaiseUntil：走到上界（或走满格数）仍判不了那么细 ⇒ 整跑判不了（不过也不不过）。
    /// </summary>
    public const string UndeterminedGridAtHi = "★ 走到了「走到上界仍判不了那么细」";

    /// <summary>
    /// Solver.Gate：场解回报 <c>Ok=false</c>（熔化／段解失败…）⇒ 判不了，**原因跟在冒号后面**。
    /// 2026-09-08 督导第 15 封：这一支原来是哑的，0.8 档的「起点熔化」被印成「场解不收敛」。
    /// </summary>
    public const string EvalNotOk = "★ 走到了「场解回报失败 ⇒ 判不了」";

    /// <summary>
    /// Solver.MeltFloor：场熔了 ⇒ **停**，「该解不存在」，板厚一位不动（用户 2026-09-09：熔化是判断工具，不是旋钮）。
    /// 09-08 曾是「下角因熔化上抬」（逐片二分抬板厚）与「探针态副本上抬」；按设定 J 的截面进下角后那两条路不可达，处置改判断。
    /// </summary>
    public const string MeltStop = "★ 走到了「熔化 ⇒ 该解不存在，不抬厚度」";

    /// <summary>
    /// Solver.ApplySectionFloor：约束盒下角因**按 J=10 定的截面**而上抬（用户 2026-09-08 设计因果链第 ② 步）——
    /// 后面跟着「片j a → b mm（设计电流 I，最紧截面 …）」。
    /// </summary>
    public const string JFloorRaised = "★ 下角因 J=10 截面上抬";

    /// <summary>
    /// Solver.FieldPlacement：每轮开头从**最新收敛的场**算移除优先级（导热贡献 ÷ 电流密度），
    /// 据此逐片定圆盘槽的槽心角与舌孔孔心（R12，用户 2026-09-08 设计因果链第 ③ 步）——
    /// 后面跟着「片j 槽心 θ°／舌孔 x=…」。第一轮之前还没有场时也发，但注明「用默认规则」。
    /// </summary>
    public const string FieldPlacement = "★ 场定孔位";
}
