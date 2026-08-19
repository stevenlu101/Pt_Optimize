using System;
using System.Drawing;
using System.Windows.Forms;

namespace PtOptimize.UI;

/// <summary>
/// ★★★★★ 界面缩放的**唯一来源**（2026-08-17，用户：「字体实在太小，界面大小与字体大小
/// 随萤幕分辨率适度放大缩小」）。
///
/// 实况（抓 UI 看到的）：窗口写死 <c>1400×900</c>，在用户那块高分屏上只占约 36 %；
/// 参数表第一列写死 132 px，标签被切成「最小可制造壁厚 [mm」「许用电流密度 [A/mm」
/// 「焊接工艺最小厚度 [r」—— **单位都被吃掉了**，而单位恰恰是最不能猜的部分。
/// 基础字号 9 pt 在那块屏上也确实偏小。
///
/// ════ 为什么不是「按 DPI 放大」就完了 ════
///
/// 本程序已经是 <c>PerMonitorV2</c>（csproj 里设了），**Windows 已经按 DPI 缩放过一轮**。
/// 若再按物理分辨率乘一次，在 4K@200 % 上就会**放大两次**，反而更糟。
///
/// ⇒ 要缩放的是「**逻辑空间比 1080p 大多少**」，不是物理像素数：
///     逻辑宽 = 工作区物理宽 ÷ (DPI ÷ 96)
///   · 3840 宽 @100 %  ⇒ 逻辑 3840 ⇒ 屏很宽，该放大
///   · 1920 宽 @150 %  ⇒ 逻辑 1280 ⇒ Windows 已经放大过了，**不该再放**
///   两种情形物理像素可能一样，但该不该放大**正好相反** —— 这就是必须用逻辑尺寸的原因。
///
/// ⚠ 所有写死的像素（列宽、分割位置、控件宽高）都要过 <see cref="S"/>，
///   否则字放大了而格子没放大，只会挤得更难看 —— 那比不放大更糟。
/// </summary>
internal static class UiScale
{
    /// <summary>基准逻辑宽度：按它算「这块屏比基准宽多少」。1600 = 常见笔电/1080p 的可用宽度。</summary>
    private const float BaseLogicalWidth = 1600f;

    private static float _k = 1.0f;
    private static bool _init;

    /// <summary>缩放系数，1.00–1.70。1.00 = 与基准屏相当，不放大。</summary>
    public static float K { get { EnsureInit(); return _k; } }

    /// <summary>基础字号 pt（界面通用）。</summary>
    public static float FontPt => (float)Math.Round(9f * K, 1);

    /// <summary>等宽字号 pt（输出框、判据表 —— 那里靠对齐吃饭，比通用字号略小一点点）。</summary>
    public static float MonoPt => (float)Math.Round(9.5f * K, 1);

    /// <summary>把一个「按基准屏设计的像素值」换算到当前屏。</summary>
    public static int S(int px) { EnsureInit(); return (int)Math.Round(px * _k); }

    /// <summary>同上，浮点。</summary>
    public static float Sf(float px) { EnsureInit(); return px * K; }

    private static void EnsureInit()
    {
        if (_init) return;
        _init = true;
        try
        {
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
            // DPI：用一个临时控件问当前的 DeviceDpi（PerMonitorV2 下这是真实值）
            float dpiScale;
            using (var probe = new Control()) dpiScale = Math.Max(1f, probe.DeviceDpi / 96f);
            float logicalW = wa.Width / dpiScale;
            _k = Math.Clamp(logicalW / BaseLogicalWidth, 1.0f, 1.7f);
        }
        catch { _k = 1.0f; }   // 取不到就按不放大处理，绝不因为缩放算不出来而起不来
    }

    /// <summary>
    /// 窗口初始大小：工作区的一个比例，并夹在「不小于基准」与「不超出工作区」之间。
    /// 直接按比例取，**与 DPI 无关** —— 屏多大就占多大的一块，这一维不需要 K。
    /// </summary>
    public static Size WindowSize(double frac = 0.80)
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
        int w = (int)(wa.Width * frac), h = (int)(wa.Height * frac);
        // ⚠ 不能写 Math.Clamp(w, 1100, wa.Width)：小屏上 1100 > wa.Width，
        //   而 Math.Clamp 在 min > max 时**直接抛 ArgumentException** ——
        //   那会让程序在小屏上根本起不来。先取「想要的下限」再被屏幕上限压住。
        w = Math.Min(Math.Max(w, 1100), wa.Width);
        h = Math.Min(Math.Max(h, 700), wa.Height);
        return new Size(w, h);
    }

    /// <summary>通用界面字体（中文优先 Microsoft YaHei UI）。</summary>
    public static Font Ui(FontStyle st = FontStyle.Regular) =>
        new("Microsoft YaHei UI", FontPt, st);

    /// <summary>
    /// ★★★★★ 等宽字体 —— 判据表与报告靠它对齐（2026-08-18 换掉 Consolas）。
    ///
    /// 病根（用户反馈「这些字行都歪扭扭的」，并问「有其它物件吗？可以让字行对齐」）：
    /// **Consolas 没有中文字形**。遇到中文时 Windows 会**回落到另一套字体**，
    /// 而那套字体的中文宽度**不是** Consolas 半角的整两倍。
    /// ⇒ 「中文算 2 个字宽」这个前提**从根上就不成立**，
    ///   于是按它补齐的列，中文越多偏得越远 —— 法兰表里
    ///   「入口」（2 个中文）与「HC1|HC2」（7 个 ASCII）就是这么错开的。
    ///
    /// **答案不是换控件，是换字体。** 用一个**本身含中文的真等宽字体**：
    /// ASCII 恰好半角、中文恰好全角，2:1 由字体保证，
    /// <see cref="TextFmt.Width"/> 的模型这才成立。
    ///
    /// 候选按优先级排；本机实测 NSimSun 在。都没有才退回 Consolas（那就还会歪，
    /// 但至少不会因为字体缺失而显示成方块）。
    /// </summary>
    public static Font Mono(FontStyle st = FontStyle.Regular) =>
        new(MonoFamily, MonoPt, st);

    private static string? _monoFamily;

    /// <summary>实际选中的等宽字族。</summary>
    public static string MonoFamily
    {
        get
        {
            if (_monoFamily is not null) return _monoFamily;
            // 顺序即优先级：前两个是开源等宽中文字体（若用户装了更好），NSimSun 是 Windows 自带
            string[] want = { "Sarasa Mono SC", "Sarasa Term SC", "NSimSun", "MS Gothic", "Consolas" };
            try
            {
                using var col = new System.Drawing.Text.InstalledFontCollection();
                var have = new HashSet<string>(col.Families.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
                _monoFamily = want.FirstOrDefault(have.Contains) ?? "Consolas";
            }
            catch { _monoFamily = "Consolas"; }
            return _monoFamily;
        }
    }
}
