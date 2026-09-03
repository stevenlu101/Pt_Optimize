namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **参数表分类名的唯一来源**（2026-09-03）。
///
/// 分类名此前写在**两个地方**：<c>DesignInputs</c> 的 53 个 <c>[Category(...)]</c>，
/// 与 <c>Flow.Params</c> 的九条登记。2026-09-03 把分类名从链代号
/// （「3 A·B·C 共用 — 保温与表面」「9 ✗ 对整线链无效」—— 工程师看不懂）
/// 改成人话时只改了前者，界面接线测试当场红：
/// <code>
///   ✗ 每个可见参数的分类都在 Flow.Params 里登记
///     ★ 没归属：TSetC「8 ✗ 改了没用 —— 由「整线核算」页上的控件决定」
/// </code>
/// 门拦住了，但这是**同一个数两处来源**的标准形态 —— 它迟早会漏一次没被拦住。
/// ⇒ 收成常数，两边引用同一个。<c>[Category]</c> 要编译期常量，所以必须是 <c>const</c>。
///
/// ⚠ 编号前缀（1..9）是**排序用**的，PropertyGrid 按类别名排。改名可以，别动编号。
/// ⚠ 8/9 两类标 <c>✗</c>：它们摆在参数表里但**改了不起作用**。
///   名字必须自己说清「为什么没用」—— 光标个 ✗ 等于让人猜。
/// </summary>
public static class ParamCat
{
    public const string 工艺条件 = "1 工艺条件";
    public const string 电气 = "2 电气";
    public const string 保温与表面 = "3 保温与表面";
    public const string 玻璃物性 = "4 玻璃物性";
    public const string 管几何 = "5 管几何";
    public const string 法兰与铜排 = "6 法兰与铜排";
    public const string 数值 = "7 数值（网格）";

    /// <summary>页面控件接管：改这里没用，真正生效的是「整线核算」页上的同名控件。</summary>
    public const string 页面接管 = "8 ✗ 改了没用 —— 由「整线核算」页上的控件决定";

    /// <summary>程序自己算：改这里没用，值由求解器强制取（如壁厚）。</summary>
    public const string 程序算出 = "9 ✗ 改了没用 —— 由程序自己算出来";
}
