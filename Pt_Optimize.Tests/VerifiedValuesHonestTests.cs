using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **档里的「网格无关复核值」不许骗人**（2026-09-02）。
///
/// ══ 这组字段是怎么来的、又怎么立刻出事的
///
/// <c>Verified*</c> 是 2026-08-30 才加的：说明书原来只显示**导航网格（2 mm）**上的
/// 记录值，而工程师无从知道那是粗网格上的数（实测同一设计能差 3 K 以上，限值 10）。
/// ⇒ 加一列「网格无关复核」，把加密到判据不再变之后的数并排显示。
///
/// **加完当天就出了事**：0.8 档填进去的 7.950 是**换 CG 之前**跑的
/// （CG 那一改把停机判据从「步长」换成「真残差」，解的位置微移过），
/// 而说明书把它当成「加密到位、可信」那一列在显示。09-02 用当前代码重测：
/// <code>
///   0.8 档   ③ 法兰增量温降   档里写 7.950   实测 10.33   限值 10  ⇒ **越限**
/// </code>
/// 不是微移，是过线。
///
/// ══ 为什么这条门在「禁掉档载入」之后**更重要**
///
/// 用户 2026-09-02：「工程师使用的 APP 只有 3DM 与 UI 输入这两个入口，
/// 之前已经禁掉『档载入、seeds』的方式」。⇒ 设计记录只剩一个用途：**校正计算流程**
/// （HANDOVER §⑬）。而**一个基准值本身是错的档，校正不了任何东西** ——
/// 它唯一的职责就是当尺子，尺子刻度错了就全废。
///
/// ══ 本门验不了「数对不对」（那要跑几分钟的整线解），验的是**诚不诚实**
///
/// 三条，都是「档必须自己把话说全」：成组、说得出口径、越限要声明。
/// </summary>
public class VerifiedValuesHonestTests
{
    private static DesignSpec[] Live =>
        DesignSpec.All.Where(d => d.Invalid.Length == 0).ToArray();

    private static bool HasVerify(DesignSpec d) => !double.IsNaN(d.VerifiedMeshMm);

    /// <summary>
    /// ★ 一：**成组出现**。只填一半 = 说明书上一列有数一列「—」，
    /// 读的人会以为那条判据「不随网格变」，而其实只是没填。
    /// </summary>
    [Fact]
    public void 复核值要么整组都有要么整组都没有()
    {
        foreach (var d in Live)
        {
            bool[] has =
            {
                HasVerify(d),
                !double.IsNaN(d.VerifiedFlangeDipK),
                !double.IsNaN(d.VerifiedHoleFluxW),
                !double.IsNaN(d.VerifiedDiscOverK),
            };
            Assert.True(has.All(x => x) || has.All(x => !x),
                $"档「{d.Name}」的复核值只填了一部分 —— "
                + "说明书上会出现「有数 / —」混排，而「—」看起来像「这条不随网格变」");
        }
    }

    /// <summary>
    /// ★★ 二：**加密之后判据只会变差，不会变好**。
    ///
    /// 实测两档都是这个方向（导航网格 → 复核）：
    /// <code>
    ///   0.6   ③ 6.124 → 9.453      0.8   ③ 4.720 → 7.950（旧代码）/ 10.33（当前）
    /// </code>
    /// 物理上说得通：粗网格抹平了孔边与焊脚那一圈的梯度，把最不利处算轻了。
    /// ⇒ 若某档的复核值**比记录值还好**，那多半是抄反了或口径搞错了 ——
    ///   本门不判它一定错，但要求档**自己解释**（VerifiedNote 非空）。
    /// </summary>
    [Fact]
    public void 复核值比记录值好时必须给出解释()
    {
        foreach (var d in Live.Where(HasVerify))
        {
            // ③ 是「越小越好」
            if (d.VerifiedFlangeDipK < d.FlangeDipK - 1e-9)
                Assert.True(d.VerifiedNote.Length > 0,
                    $"档「{d.Name}」的复核值（{d.VerifiedFlangeDipK:0.000}）比记录值"
                    + $"（{d.FlangeDipK:0.000}）还好，与两档的实测方向相反，而档里一个字没解释");
        }
    }

    /// <summary>
    /// ★★★ 三（本门的主条）：**复核值越限时，档必须自己说出来**。
    ///
    /// 限值从 <see cref="LineCase"/> 取 —— 判据的限值全仓只有那一个来源，
    /// 在这里再抄一个 10 就是「印出来的 ≠ 判的」，本仓库为这件事栽过不止一次。
    ///
    /// 一个复核值已经过线、而档里既不声明失效也不写一句话的档，
    /// 会被说明书原样排成一张带裕度条的表 —— **看起来就是答案**。
    /// </summary>
    [Fact]
    public void 复核值越限的档必须自己声明()
    {
        var lim = new LineCase();   // 限值的唯一来源
        foreach (var d in Live.Where(HasVerify))
        {
            bool over = d.VerifiedFlangeDipK > lim.RootDeltaMaxK
                     || d.VerifiedDiscOverK  > lim.DiscOverTempMaxK
                     || d.VerifiedHoleFluxW  < 0;          // 管孔净流入须为正
            if (!over) continue;

            Assert.True(d.VerifiedNote.Length > 0,
                $"档「{d.Name}」的复核值已经越限（③ {d.VerifiedFlangeDipK:0.000} / "
                + $"限 {lim.RootDeltaMaxK:0.0}），而档里一个字都没说 —— "
                + "说明书会把它排成一张带裕度条的表，看起来就是答案");

            Assert.True(
                d.VerifiedNote.Contains("越限", StringComparison.Ordinal)
             || d.VerifiedNote.Contains("超", StringComparison.Ordinal)
             || d.VerifiedNote.Contains("不过", StringComparison.Ordinal),
                $"档「{d.Name}」的复核值越限，而 VerifiedNote 没把这件事说出口："
                + d.VerifiedNote);
        }
    }

    /// <summary>
    /// ★ 自证：现役档里**真的有**填了复核值的，否则上面三条全是空转。
    /// 空集恒真是本仓库的老毛病（见 Criteria 里那段「九条参考量共用一个代号」）。
    /// </summary>
    [Fact]
    public void 自证_现役档里确实有复核值()
    {
        Assert.NotEmpty(Live);
        Assert.True(Live.Any(HasVerify),
            "没有一个现役档填了复核值 —— 上面三条断言全部空转，不能算通过");
    }

    /// <summary>
    /// ★ 自证：限值真的读到了东西。<see cref="LineCase"/> 换了默认值也不会让本门失灵。
    /// </summary>
    [Fact]
    public void 自证_限值读得到()
    {
        var lim = new LineCase();
        Assert.True(lim.RootDeltaMaxK > 0, "③ 的限值读不到 —— 越限判定会恒假");
        Assert.True(lim.DiscOverTempMaxK > 0, "圆盘区最高温的限值读不到");
    }
}

/// <summary>
/// ★★★★ **裕度条不许把「不过」画成「还过」**（2026-09-02）。
///
/// `ManualPage.Bar()` 原来两行都会：
/// <code>
///   ① Math.Clamp(raw/limit*100, 2, 99)   ③ = 10.33 / 限 10 ⇒ raw = −0.33
///                                        ⇒ Clamp(−3.3, 2, 99) = 2 ⇒ 「余量 2 %」
///   ② 限值为 0 那一支**不看 actual**      净流入为负（烧断方向）也写「方向安全」
/// </code>
/// 两条都是同一种病：**把失败渲染成通过**。而这张表有排版、有裕度条，
/// 看起来就是答案 —— 越像答案的东西，说错话的代价越大。
/// </summary>
public class MarginBarTests
{
    private static string Bar(double actual, double limit, bool less)
    {
        var m = typeof(PtOptimize.UI.ManualPage).GetMethod("Bar",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(m);
        return (string)m!.Invoke(null, new object[] { actual, limit, less })!;
    }

    [Fact]
    public void 越限画成越限而不是余量两个百分点()
    {
        string over = Bar(10.33, 10.0, true);     // ③ 超上限
        Assert.Contains("越限", over);
        Assert.DoesNotContain("2 %", over);
    }

    [Fact]
    public void 方向性判据为负时不许说方向安全()
    {
        string bad = Bar(-883.8, 0.0, false);     // 管孔净流入 须为正，实测倒灌
        Assert.Contains("越限", bad);
        Assert.DoesNotContain("方向安全", bad);
    }

    /// <summary>
    /// ★★ **刚好贴着限值时要印 0 %，不是 2 %**（2026-09-02 抓图抓到）。
    /// 舌片自由段 100.000 / 下界 100.00 真实余量是 0，而 Clamp(pct, 2, 99) 把**标签**
    /// 也夹成了「余量 2 %」—— 条子需要宽度下限（太窄看不见），数字不需要。
    /// 「刚好贴着下界」被说成「还有一点」，与「把不过画成还过」是同一族。
    /// </summary>
    [Fact]
    public void 刚好贴着限值时印零而不是二()
    {
        string edge = Bar(100.0, 100.0, false);   // 舌片自由段，恰在下界
        Assert.Contains("0 %", edge);
        Assert.DoesNotContain("2 %", edge);
        Assert.DoesNotContain("越限", edge);       // 贴着下界仍然是过
    }

    /// <summary>★ 自证：正常通过的情形照旧，别把门修成「什么都叫越限」。</summary>
    [Fact]
    public void 自证_通过的情形照旧()
    {
        Assert.DoesNotContain("越限", Bar(4.72, 10.0, true));      // 余量 53 %
        Assert.Contains("%", Bar(4.72, 10.0, true));
        Assert.Contains("方向安全", Bar(1.12, 0.0, false));         // 净流入为正
    }
}

/// <summary>
/// ★★★★★ **储存结果这一步：程序不替工程师否决，但必须把风险记下来**（2026-09-02 用户拍板）。
///
/// 用户原话：「计算结果是如何就如何，超标就显示提醒，最终让工程师判断合格与否
/// （风险由工程师判断）；若工程师判断可承担风险，工程师就可储存计算结果与出图」。
///
/// 改之前：`"final.save" =&gt; Fresh &amp;&amp; AllOk`，而 `Blocks` 里 `NotApplicable` 排在门禁之前
/// ⇒ 连「我知道风险，越关进入」都绕不过 ⇒ **超标的结果连存都存不下来**。
/// </summary>
public class SaveRiskTests
{
    private static string Ui(string f) => System.IO.File.ReadAllText(
        System.IO.Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "UI", f));

    [Fact]
    public void 另存不再要求判据全过()
    {
        string s = Ui("LineDesignPage.cs");
        Assert.Contains("\"final.save\" => Shared is { Fresh: true },", s);
        // 反面：旧条件不许回来
        Assert.DoesNotContain("\"final.save\" => Shared is { Fresh: true, Last.AllOk: true }", s);
    }

    /// <summary>★ 仍要硬拦「参数动过了」—— 那不是风险判断，是**错**（存的会是上一组参数的解）。</summary>
    [Fact]
    public void 参数动过仍然硬拦()
    {
        string s = Ui("LineDesignPage.cs");
        Assert.Contains("if (Shared is not { Last: { } r } f || !f.Fresh)", s);
        Assert.Contains("存下去的会是**上一组参数**的解", s);
    }

    /// <summary>★★ 超标要**列出来**并要一次明确确认，默认按钮是「否」。</summary>
    [Fact]
    public void 超标要列清楚并要明确确认()
    {
        string s = Ui("LineDesignPage.cs");
        Assert.Contains("要不要存，由你判断 —— 风险你承担。", s);
        Assert.Contains("MessageBoxButtons.YesNo", s);
        Assert.Contains("MessageBoxDefaultButton.Button2", s);   // 默认「否」，不许手滑
        Assert.Contains("条判据没过", s);
        // 没加密复算过也要说 —— 那是「这个数准不准」，与超没超标是两件事
        Assert.Contains("还没加密复算", s);
    }

    /// <summary>★★★ 本条是「风险由工程师判断」成立的前提：**风险要被写进档**。</summary>
    [Fact]
    public void 有问题时把话写进档()
    {
        string s = Ui("LineDesignPage.cs");
        Assert.Contains("d.VerifiedNote = over.Length > 0 || !verified", s);
        Assert.Contains("由工程师判断后仍决定保存", s);
        // 加密复算过的话，口径也要一起存（判据值是哪张网格上的）
        Assert.Contains("d.VerifiedMeshMm      = mv.FineMm;", s);
    }
}
