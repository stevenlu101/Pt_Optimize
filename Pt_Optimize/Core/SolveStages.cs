using System;

namespace PtOptimize.Core;

/// <summary>
/// ★ 解法阶段（用户 2026-09-09 晚：「跑的过程把①②③④⑤的状态显示出来，让工程师知道 APP 正在干啥」）。
///
/// 求解器与加密复算只往进度里丢文字行；这里把每一行**认成**五个阶段之一，界面（<c>SolveStageStrip</c>）
/// 与命令行（<c>--solve</c>）用同一份认法 —— 对话里说的五步就是 APP 里显示的五步，不许两套。
///
/// 五步（按「核算整线」真正跑的顺序）：
///   ① 设计电流：20 °C/h 空管升温所需管电流（闭式）
///   ② 约束盒下角：舌片厚 I/(J·舌宽)、基板按 J 截面、裸舌、无台阶（闭式；熔化只验不抬）
///   ③ 导航网格逐轮：解场 → 没过的判据抬它自己的旋钮 → 只增不减到全过
///   ④ 加密复算：网格一档档加密到判据不再变
///   ⑤ 细网格重解：在判据所在的那张网格上重新求根（搜形状的「精算」；流水线里加密不过后的下一步）
///
/// 认法只看**求解器自己印的**固定前缀（Solver／MeshVerify／搜形状精算），认不出的行不改阶段。
/// 这些前缀改了这里就要跟着改 —— `SolveStagesTests` 用真轨迹的原句钉着。
/// </summary>
public static class SolveStages
{
    public enum Stage { None = 0, DesignCurrent = 1, Corner = 2, NavRounds = 3, MeshVerify = 4, FineResolve = 5, Done = 6 }

    /// <summary>五步的编号、标题与一句话（给界面画格子、给命令行印标题）。</summary>
    public static readonly (Stage Id, string Title, string What)[] All =
    {
        (Stage.DesignCurrent, "① 设计电流",   "20 °C/h 升温所需管电流（闭式）"),
        (Stage.Corner,        "② 约束盒下角", "舌片厚 I/(J·舌宽)、基板按 J 截面、裸舌、无台阶（闭式）"),
        (Stage.NavRounds,     "③ 导航网格逐轮", "解场 → 没过的判据抬它自己的旋钮 → 只增不减到全过"),
        (Stage.MeshVerify,    "④ 加密复算",   "网格一档档加密到判据不再变"),
        (Stage.FineResolve,   "⑤ 细网格重解", "在判据所在的网格上重新求根"),
    };

    public static string Title(Stage s) => s switch
    {
        Stage.Done => "✓ 算完",
        Stage.None => "还没跑",
        _ => Array.Find(All, a => a.Id == s).Title,
    };

    /// <summary>
    /// 从一行进度／轨迹认阶段。<paramref name="current"/> = 现在所在的阶段：
    /// 「第 n 轮」这种行不带遍号，归到当前那一遍（③ 或 ⑤）；认不出 ⇒ 回 <paramref name="current"/>。
    /// </summary>
    public static Stage Of(string line, Stage current)
    {
        if (string.IsNullOrWhiteSpace(line)) return current;
        string s = StripStamp(line).TrimStart();
        // ① 设计电流（ApplySectionFloor 开头印 DesignCurrent.Describe()）
        if (s.StartsWith("设计电流（温控", StringComparison.Ordinal)) return Stage.DesignCurrent;
        // ② 下角：舌片厚闭式、J 截面上抬、闭式部分、起点
        if (s.StartsWith("★ 舌片厚按", StringComparison.Ordinal)
         || s.StartsWith("★ 下角因", StringComparison.Ordinal)
         || s.StartsWith("下角的闭式部分", StringComparison.Ordinal)
         || s.StartsWith("起点 = ", StringComparison.Ordinal)
         || s.StartsWith("传进来的旋钮值", StringComparison.Ordinal)
         || s.StartsWith("限值只从 LineCase 读", StringComparison.Ordinal)) return Stage.Corner;
        // ⑤ 第二遍（细网格上重新求根）／搜形状的精算
        if (s.StartsWith("── ", StringComparison.Ordinal) && s.Contains("（细网格", StringComparison.Ordinal)) return Stage.FineResolve;
        if (s.StartsWith("精算", StringComparison.Ordinal)) return Stage.FineResolve;
        // ③ 第一遍（导航网格）
        if (s.StartsWith("── ", StringComparison.Ordinal) && s.Contains("（导航网格）", StringComparison.Ordinal)) return Stage.NavRounds;
        // ④ 加密复算（MeshVerify）
        if (s.StartsWith("加密复算：", StringComparison.Ordinal)
         || s.StartsWith("中带确认", StringComparison.Ordinal)
         || s.StartsWith("较上一档", StringComparison.Ordinal)) return Stage.MeshVerify;
        // 逐轮行：归当前那一遍；还没进任何一遍就当第一遍
        if (IsRoundLine(s))
            return current == Stage.FineResolve ? Stage.FineResolve : Stage.NavRounds;
        return current;
    }

    /// <summary>给格子旁边那一行细节：轮数与合计只取到「板厚」之前，别把整行旋钮表塞进去。</summary>
    public static string Detail(string line)
    {
        string s = StripStamp(line).TrimStart();
        int k = s.IndexOf("　板厚", StringComparison.Ordinal);
        if (k > 0) s = s[..k];
        return s.Length > 90 ? s[..90] + "…" : s;
    }

    private static bool IsRoundLine(string s)
    {
        // 「第  2 轮　合计 2603 g …」／「第 1 轮」（搜形状精算的 Note 去掉「精算　」之后）
        if (!s.StartsWith("第", StringComparison.Ordinal)) return false;
        int i = 1;
        while (i < s.Length && (s[i] == ' ' || s[i] == '　')) i++;
        int j = i;
        while (j < s.Length && char.IsDigit(s[j])) j++;
        if (j == i) return false;
        while (j < s.Length && (s[j] == ' ' || s[j] == '　')) j++;
        return j < s.Length && s[j] == '轮';
    }

    /// <summary>去掉测试落档时加在行首的「[ 12.3 分] 」时间戳（界面进度没有它）。</summary>
    private static string StripStamp(string line)
    {
        string s = line.TrimStart();
        if (s.StartsWith("[", StringComparison.Ordinal))
        {
            int k = s.IndexOf("分]", StringComparison.Ordinal);
            if (k > 0 && k < 16) s = s[(k + 2)..];
        }
        return s;
    }
}
