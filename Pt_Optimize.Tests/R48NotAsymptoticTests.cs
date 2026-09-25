using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 门（2026-09-14，Opus 5 写；常驻数值讨论人查出三个洞之后补的）：
/// **序列不在渐近区时，不许声称「数已经不再变了」。**
///
/// ══ 三个洞（都实际发生过）
///
/// **洞一 —— 位置错了，等于没接。** 第一版把「振荡」只接到 `ConclusionStable` 上。
/// 而实测那一组（+0.841 → −1.693 → −1.350）的 Δ₂ = +0.344 ≤ 容差 0.5
/// ⇒ `Within` 为真，**根本不走 `ConclusionStable` 那条旁路** ⇒ 照样判收敛。
///
/// **洞二 —— 位没被置上。** `MeshVerify` 里 `res.Trace` 已经含当前档，
/// 第一版又 `hist.Add(now)` 补了一遍 ⇒ `prevChange` 恒等于 `change`
/// ⇒ `Oscillating` **恒 false**、`RatioR` **恒 1.0**。整个特性是 no-op，而没有任何测试看得出来。
///
/// **洞三 —— 只实现了三态里的一态。** Roache 三态（R = Δ₂/Δ₁）：0&lt;R&lt;1 收敛、R&lt;0 振荡、|R|≥1 发散。
/// 只做了振荡 ⇒「同号 + Δ 在涨 + 离限值远」仍会被判「结论稳」，而 `Converged` 又在它为真时
/// **跳过**「变化在缩小」那道检查 ⇒ 一边发散一边判收敛。
///
/// ⇒ 本组钉的是**行为**，不是源码字串：给定的数进去，出来的判定必须是什么。
/// </summary>
public class R48NotAsymptoticTests
{
    /// <summary>实测那一组：管孔净流入 +0.841 → −1.693 → −1.350（管壁 0.8，自相似加密 1.0/0.5/0.25）。</summary>
    private static MeshAdapt.Delta Measured() => new()
    {
        Name = "②′",
        Change = -1.350 - (-1.693),          // Δ₂ = +0.343
        Tol = 0.5,                            // ⇒ Within 为真 —— 正是洞一的入口
        MarginOverChange = Math.Abs(-1.350 - 0) / Math.Abs(-1.350 - (-1.693)),
        Oscillating = true,                   // Δ₁ = −2.534 与 Δ₂ = +0.343 换号
        HalfRange = (0.841 - (-1.693)) / 2,   // 1.267
        RatioR = Math.Abs((-1.350 - (-1.693)) / (-1.693 - 0.841)),   // 0.135
    };

    [Fact]
    public void 实测那一组必须被判成不在渐近区()
    {
        var d = Measured();
        Assert.True(d.Within, "前提：这一组的 Δ₂ 落在容差里 —— 洞一的入口就在这");
        Assert.True(d.Oscillating);
        Assert.True(d.NotAsymptotic);
        Assert.False(d.ConclusionStable, "不在渐近区就不许算「结论稳」");
        Assert.Equal(1.267, d.HalfRange, 3);
        Assert.Equal(1.267, d.ErrScale, 3);     // 误差尺度要用半幅，不是 |Δ₂| = 0.343
    }

    /// <summary>洞一的正题：Within 为真也不许放行。</summary>
    [Fact]
    public void 振荡时即使数值落进容差也不算收敛()
    {
        var now = new[] { Measured() };
        var was = new[] { new MeshAdapt.Delta { Name = "②′", Change = -2.534, Tol = 0.5 } };
        Assert.False(MeshAdapt.Converged(now, was),
            "Δ₂ 落进容差就放行 —— 那正是「位置错了等于没接」：Within 这条路绕过了振荡检查");
    }

    [Fact]
    public void 判词不许说数已经不再变了()
    {
        string v = MeshAdapt.Verdict(0.25, new[] { Measured() }, hitCap: false);
        Assert.StartsWith("✗", v);
        Assert.DoesNotContain("数已经不再变了", v);
        Assert.DoesNotContain("结论已经算准了", v);
        Assert.Contains("不在渐近区", v);
        Assert.Contains("振荡", v);
        Assert.Contains("至少", v);              // 三点采样的半幅是下界，不是幅度本身
    }

    /// <summary>洞三：发散（Δ 在变大）必须和振荡一样是一票否决。</summary>
    [Theory]
    [InlineData(1.00)]
    [InlineData(1.50)]
    [InlineData(0.80)]   // ≥ 0.75：剩余误差已达 3 倍动幅，外推不成立
    public void 发散或几乎不缩也算不在渐近区(double ratio)
    {
        var d = new MeshAdapt.Delta
        { Name = "②′", Change = 2.0, Tol = 0.5, MarginOverChange = 99, RatioR = ratio, HalfRange = 3.0 };
        Assert.True(d.NotAsymptotic, $"比 {ratio} 应判成不在渐近区");
        Assert.False(d.ConclusionStable);
        Assert.False(MeshAdapt.Converged(new[] { d },
            new[] { new MeshAdapt.Delta { Name = "②′", Change = 1.0, Tol = 0.5 } }));
    }

    /// <summary>
    /// 洞二的另一半：Δ 塌成 0 时 MarginOverChange → ∞，会无条件放行。
    /// 那多半是这一档恰好落在极值点上 —— 正是 2026-08-30 修掉的假收敛。
    /// </summary>
    [Fact]
    public void 变化塌成零时不许靠结论稳放行()
    {
        var d = new MeshAdapt.Delta
        { Name = "③", Change = 0.2, Tol = 1.0, MarginOverChange = 45, RatioR = 0.0004, HalfRange = 250.0 };
        // ★ 本门改了两次（2026-09-14，Opus 5），守的**性质**始终是：Δ 塌成 0 不许让加密复核判收敛。
        //   第一版：靠「r < 0.02 ⇒ 不在渐近区」（0.02 是我拍的）。
        //   第二版：删掉那条否决、给误差尺度加「半幅 / 0.1×容差」下界 —— 第一次跑是红的，门抓对了；
        //           复核又指出那个下界让 r 在门槛与分母里算两遍、且 0.1×容差下界在任何判决路径上都不生效。
        //   现行：由**阶数合理性**一票否决（r < 0.25 即实测阶数 > 格式最高阶 2，出处 ASME V&V 20 / Roache）。
        //   ⚠ 所以改钉**判决**，不钉中间量：变化 0.2 已高于噪声底 0.1，却从上一步的 500 塌到 0.2 ——
        //     这一组的 Within 是**真**的（0.2 ≤ 1.0），旧写法会经 Within 直接放行，那才是要堵的洞。
        Assert.True(d.Within, "前提：这一组落在容差里 —— 洞就在这，Within 会放行它");
        Assert.True(d.NotAsymptotic, "实测阶数 log₂(1/0.0004) ≈ 11，远超格式最高阶 2 ⇒ 必须判不在渐近区");
        Assert.Contains("实测阶数", d.WhyNotAsymptotic);
        var prev = new MeshAdapt.Delta { Name = "③", Change = 500, Tol = 1.0 };
        Assert.False(MeshAdapt.Converged(new[] { d }, new[] { prev }),
            "Δ 塌成 0 不许让加密复核判收敛 —— 这是本门守的性质，机制换了性质不许丢");
    }

    /// <summary>对照：同样塌成 0，但变化落在噪声底里 —— 那时比值是在比噪声，不否决，由「落进容差」正常放行。</summary>
    [Fact]
    public void 变化塌进噪声底时不否决()
    {
        var d = new MeshAdapt.Delta { Name = "③", Change = 0.001, Tol = 1.0, RatioR = 0.0004 };
        Assert.False(d.NotAsymptotic, "变化 0.001 ≤ 噪声底 0.1 ⇒ 比值是在比噪声，任何否决都没有意义");
        Assert.True(d.Within);
        Assert.Equal(0.001, d.ErrScale, 9);   // 一般分支就是 |Δ|，不再给下界（下界会让 r 算两遍）
    }

    /// <summary>门槛要按实测比值折算，不是钉死 3；而且 r ≥ 0.75 一律不许放行。</summary>
    [Theory]
    [InlineData(0.50, 3.0)]     // r/(1−r) = 1 ⇒ 安全系数 3
    [InlineData(0.60, 4.5)]     // r/(1−r) = 1.5 ⇒ 4.5
    [InlineData(0.70, 7.0)]     // r/(1−r) ≈ 2.33 ⇒ 7
    public void 结论稳的门槛按实测比值折算(double r, double want)
    {
        var d = new MeshAdapt.Delta { Name = "x", Change = 1.0, Tol = 0.1, RatioR = r };
        Assert.Equal(want, d.StableFactor, 2);
    }

    /// <summary>量不到比值（不足三档）时退回 3，且不算「不在渐近区」——那由「至少三档」那道门管。</summary>
    [Fact]
    public void 量不到比值时退回三倍且不算不在渐近区()
    {
        var d = new MeshAdapt.Delta { Name = "x", Change = 1.0, Tol = 0.1, RatioR = double.NaN };
        Assert.Equal(3.0, d.StableFactor, 6);
        Assert.False(d.NotAsymptotic);
    }

    /// <summary>
    /// ★★★★★ 终止分支：**到上限且仍在摆 ⇒ 判不了**，与「还在爬」必须说成两句话。
    /// 振荡不会因为再加密就消失 ⇒ 那是终态，叫人「再跑久一点」是误导。
    /// </summary>
    [Fact]
    public void 到上限仍在摆时判词要说判不了而不是还在爬()
    {
        var d = Measured();
        string climbing = MeshAdapt.Verdict(0.25, new[] { d }, hitCap: false);
        string terminal = MeshAdapt.Verdict(0.25, new[] { d }, hitCap: true);
        Assert.NotEqual(climbing, terminal);                 // ← 此前这两句一模一样
        Assert.Contains("判不了", terminal);
        Assert.Contains("不要把它读成", terminal);           // 既不是「过」也不是「不过」
        Assert.Contains("【下一步】", terminal);             // 说了判不了就要给动作
        Assert.DoesNotContain("判不了", climbing);
    }

    /// <summary>
    /// 接线门：新状态位必须**被下游读到**。本仓库当天已经栽了三次「赋了值没人读」，
    /// 这一条专治第四次：`MeshVerify` 要算出它，`VerifyMeshAsync` 要按它分支说话。
    /// </summary>
    [Fact]
    public void 判不了这个位必须接到界面上()
    {
        string mv = Src("Pt_Optimize/Core/MeshVerify.cs");
        Assert.Contains("public bool Undecidable;", mv);
        Assert.Contains("res.Undecidable = res.HitCellCap && !res.Converged", mv);

        string ui = Src("Pt_Optimize/UI/LineDesignPage.cs");
        Assert.Contains("if (res.Undecidable)", ui);
        // 且必须排在「没验过」那句之前 —— 否则判不了会被说成「还在随网格变」
        int u = ui.IndexOf("if (res.Undecidable)", StringComparison.Ordinal);
        int n = ui.IndexOf("else if (!res.Converged)", StringComparison.Ordinal);
        Assert.True(u > 0 && n > u, "「判不了」必须排在「没验过」之前，否则两种处境会被说成同一句");
    }

    /// <summary>
    /// 噪声底豁免：变化本身已落在噪声里时，比值是在比噪声，不许据此否决整趟阶梯。
    /// 实例是本项目实测的 ②″：−0.017 → −0.008 → −0.004，|Δ₂| = 0.004，
    /// 而 0.1×容差 = 0.02 —— 只有噪声门槛的五分之一。
    /// </summary>
    [Fact]
    public void 变化落在噪声底里时不许判成不在渐近区()
    {
        var d = new MeshAdapt.Delta
        { Name = "②″", Change = 0.004, Tol = 0.2, Oscillating = true, RatioR = 0.444, HalfRange = 0.0065 };
        Assert.True(Math.Abs(d.Change) <= 0.1 * d.Tol, "前提：这一条的变化落在噪声底里");
        Assert.False(d.NotAsymptotic,
            "收敛得最好的那一条反而把整趟阶梯永久否掉 —— 噪声底豁免就是防这个");
    }

    /// <summary>误差尺度要**按态分支**：振荡用半幅、慢收敛用 Δ·r/(1−r)、发散不给数。</summary>
    [Theory]
    [InlineData(0.80, false, 4.0)]    // 慢收敛：1.0 × 0.8/0.2 = 4.0（半幅会给 1.13，偏小 3.6 倍）
    [InlineData(0.90, false, 9.0)]
    public void 误差尺度按态分支(double r, bool osc, double want)
    {
        var d = new MeshAdapt.Delta
        { Name = "x", Change = 1.0, Tol = 0.1, Oscillating = osc, RatioR = r, HalfRange = 1.13 };
        Assert.Equal(want, d.ErrScale, 6);
    }

    [Fact]
    public void 发散时不给误差数字()
    {
        var d = new MeshAdapt.Delta { Name = "x", Change = 1.0, Tol = 0.1, RatioR = 1.4, HalfRange = 2.0 };
        Assert.True(double.IsNaN(d.ErrScale), "剩余误差无界，任何数字都不该印");
    }

    /// <summary>
    /// 源码门（洞二专用）：`MeshVerify` 里不许再把当前档补进 hist ——
    /// `res.Trace` 在算差值之前就已经 Add 过了，补一遍会让 prevChange 恒等于 change。
    /// </summary>
    [Fact]
    public void 不许把当前档重复加进历史()
    {
        string s = Src("Pt_Optimize/Core/MeshVerify.cs");
        int i = s.IndexOf("(bool osc, double half, double ratio) Shape(", StringComparison.Ordinal);
        Assert.True(i > 0, "找不到算振荡的那一段 —— 门失去了守护对象，先修门");
        string body = s.Substring(i, Math.Min(2400, s.Length - i));
        // ★ 剔掉注释再查（2026-09-14，Opus 5）：MeshVerify 里解释这个洞的注释本身就写着「hist.Add(now)」，
        //   第一版门没剔注释，于是被那句解释弄红 —— 门在查代码，不该被说明文字触发。
        string code = string.Join("\n", body.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Assert.DoesNotContain("hist.Add(now)", code);
        Assert.Contains("res.Trace.Select(pick)", body);
    }

    private static string Src(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, rel));
    }
}
