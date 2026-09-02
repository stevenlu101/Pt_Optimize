using System.Reflection;
using System.Windows.Forms;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **门禁横幅上的字，工程师看到的就是屏幕上那一串**（2026-09-02）。
///
/// ══ 怎么发现的：抓图
///
/// 用户 2026-09-02：「改 UI 排版记得自己抓图验证」。跑 <c>--cli --uishot</c> 抓下来一看，
/// ⑤ 交付那一格的红色门禁横幅上写着：
/// <code>
///   为什么：交付件不能是一个自己声明不成立的设计，**也不能是一个没人验过准不准的数**。
/// </code>
/// **加粗标记原样露在屏幕上。** 而那句话正是我 2026-08-30 加出图闸时写的。
///
/// 同一行还有第二个：<c>b.Name</c> 是判据的 <c>Key</c>（如「②′管孔净流入」）——
/// 门被卡住时它会把**判据代号**直接印到横幅上，而用户明令「UI 内严禁使用 ②′ 这类的表示」。
///
/// ══ 为什么源码里看不出来
///
/// 源码写的是 <c>$"为什么：{gate.Why}"</c> —— 看不出 <c>gate.Why</c> 里有星号，
/// 也看不出 <c>b.Name</c> 带代号。<see cref="StagePanel"/> 别处每一处都规规矩矩走了
/// <c>Plain()</c>，**只有这一条漏了**。这类漏法只有两种抓法：抓图，或者像本门一样
/// **真造一个面板去问它**。
///
/// 与 <see cref="LongRunProgressTests"/> 里那条「面板真的把进度画出来」同源：
/// 进度条的 <c>Height = 16</c> 也是设了没生效（实际 0 px），也是真造面板才问出来的。
/// </summary>
public class StagePanelPlainTextTests
{
    private static string Label(StagePanel p, string field) =>
        ((Label)typeof(StagePanel)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(p)!).Text;

    /// <summary>
    /// 造一个「④→⑤ 的门关着」的真实状态：解出来了、收敛了、参数没动过，
    /// 但**判据没全过** ⇒ Gate 会走到 RequireAllOk 那一支，
    /// 把 <c>LockedWhy</c>（含加粗标记）与 <c>Worst</c>（判据 Key，含代号）都拼进横幅。
    /// </summary>
    /// <summary>
    /// 造一个「进不去交付」的真实状态。
    ///
    /// ⚠ 2026-09-02 改口径：交付的门**不再要求判据全过**（用户拍板「结果如何就如何，
    ///   超标显示提醒，风险由工程师判断」）⇒ 用「判据没过」已经锁不住这一格了。
    ///   现在锁得住的只有「没解出来」与「参数动过了」—— 那不是风险，是对不上。
    /// </summary>
    private static StagePanel LockedAtDelivery()
    {
        var st = new FlowState();
        st.Last = new LineResult { Ok = true, Converged = false };   // 没收敛 ⇒ 门关着
        st.CurrentSnap = st.SolvedSnap = "同一个快照";
        var panel = new StagePanel(st);
        panel.SetStage(StageId.交付);
        return panel;
    }

    [Fact]
    public void 门禁横幅上不出现加粗标记()
    {
        string banner = Label(LockedAtDelivery(), "_banner");
        Assert.NotEqual("", banner);          // 空的话下面这条恒真 —— 那不算通过
        Assert.DoesNotContain("**", banner);
    }

    /// <summary>
    /// ★ 判据代号那条**现在走不到了**，但守卫要留着。
    ///
    /// `b.Name` 只在 `gate.Blocking` 非空时进横幅，而那要求门带 RequiredChecks 或
    /// RequireAllOk —— 2026-09-02 重排之后**没有任何一道门带这两样**（原 ①→② 那道门
    /// 随阶段一起没了，交付的门也按用户拍板去掉了 AllOk）⇒ 运行时构造不出那个状态。
    ///
    /// ⚠ 所以这里改成钉**源码**：剥壳那一步必须在。哪天再加一道带判据的门，
    ///   代号就会从这条路漏出去 —— 那时守卫已经在位，而不是要等人想起来补。
    /// </summary>
    [Fact]
    public void 门禁横幅剥判据代号的那一步还在()
    {
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "StagePanel.cs"));
        Assert.Contains("Criteria.Plain(b.Name)", src);
        Assert.Contains("Plain(gate.Why)", src);
        Assert.Contains("Plain(gate.How)", src);
    }

    /// <summary>
    /// ★ 自证：这个状态下门确实是锁着的。否则上面那条面对的是空字符串，会一直报通过。
    /// </summary>
    [Fact]
    public void 自证_这个状态下门确实是锁着的()
    {
        string banner = Label(LockedAtDelivery(), "_banner");
        Assert.Contains("还没解锁", banner);
        Assert.Contains("解得出来", banner);   // LockedWhy 真的进来了
    }
}

/// <summary>
/// ★★★★ **页顶横幅不许把话说一半**（2026-09-02 抓图抓到，改了两次才对）。
///
/// ① 原来 <c>AutoSize=false + Height=S(34)</c>（**一行**的高度）⇒ 超过一行的横幅被**静默切掉**。
///    实况：改「设计记录」那条（三行）之后，屏幕上停在「由 APP 自己解出来，不」。
/// ② 第一次改成 <c>AutoSize=true</c> **还是错的** —— Label 的 AutoSize 按单行首选宽度算，
///    长成一条很宽的单行、横向被容器裁掉；抓图看到第一行断在「不从…」。
///
/// 两次都只有**抓图**才看得见：源码里写着「AutoSize=true」，看不出它横向会被裁。
/// 本门是那张图的自动化版本 —— 真造一个横幅，给它一个宽度，问它够不够高。
/// </summary>
public class BannerFitsTests
{
    private static System.Windows.Forms.Label Make(string text)
    {
        var m = typeof(PtOptimize.UI.MainForm).GetMethod("Banner",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        return (System.Windows.Forms.Label)m!.Invoke(null, new object[] { text })!;
    }

    [Fact]
    public void 长横幅会自己长高而不是被切掉()
    {
        const string longText =
            "这一页不是设计的起点。设计从「整线核算」页开始 —— 那里有两个入口：填 UI 参数，" +
            "或读一张 .3dm 图纸。厚度、保温、环倍率这些由 APP 自己解出来，不从档里抄。" +
            "本页是校正与存档：拿已归档的设计复算一遍，看计算流程还准不准；" +
            "以及把当前这个全过的解存成新档。";

        var one = Make("短的一行");
        var many = Make(longText);
        // 给同一个宽度（Dock 之后的真实宽度由容器给，这里手动设以触发 Fit）
        one.Width = many.Width = 900;

        Assert.True(many.Height > one.Height,
            $"长横幅没有长高（长 {many.Height} px vs 短 {one.Height} px）—— 它会被静默切掉半句话");

        // ★ 关键：高度要够放下**折行之后**的全部文字，不能只多一点
        int need = System.Windows.Forms.TextRenderer.MeasureText(
            many.Text, many.Font,
            new System.Drawing.Size(many.Width - many.Padding.Horizontal, int.MaxValue),
            System.Windows.Forms.TextFormatFlags.WordBreak).Height;
        Assert.True(many.Height >= need + many.Padding.Vertical,
            $"高度 {many.Height} px 放不下折行后需要的 {need + many.Padding.Vertical} px");
    }

    /// <summary>★ 自证：短横幅的观感不变（别把门修成「所有横幅都变高」）。</summary>
    [Fact]
    public void 自证_短横幅仍是一行的高度()
    {
        var one = Make("短的一行");
        one.Width = 900;
        // UiScale 是 internal ⇒ 反射取，别在测试里另抄一个 34（那就是「同一个数两处来源」）
        var t = typeof(PtOptimize.UI.Flow).Assembly.GetType("PtOptimize.UI.UiScale");
        Assert.NotNull(t);
        int oneLine = (int)t!.GetMethod("S", BindingFlags.Public | BindingFlags.Static)!
                              .Invoke(null, new object[] { 34 })!;
        Assert.Equal(oneLine, one.Height);
    }
}
