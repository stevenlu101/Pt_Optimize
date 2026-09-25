using System;
using System.IO;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **过热 → 降电流密度 → 驱动搜厚度／搜形状**（用户 2026-09-03 定的口径）。
///
/// 用户原话：「过热（严重烧毁）表示电流密度过大，应该加大电流的截面积、降电流密度，
/// 所以可能是增厚或增宽（这时就需透过搜形状／搜厚度来解决）。在 APP 里，工程师是给
/// 一个**可能用不了**的法兰，APP 要提供一个**首先能用的且铂金用量最少**的方案出来，
/// 而不是一句话就判死刑结案了。」
///
/// ══ 在此之前这条通路是**断的**
///
/// <c>SolveByLevel</c> 两层，**没有一层因为「过热」去加厚**：
/// <code>
///   外层 t[j]       由抽热误差（②′/③）驱动 —— 不看局部温度；③ 抽太多时倾向**削薄**
///   内层 scale[j][m] 只改比例，调完几何平均归一化 ⇒ 整片平均厚度不变
///                    （等厚板 1 级时 norm ≡ adj[0]，连挪都没得挪）
/// </code>
/// 于是一超熔点只剩「判死刑」：
/// <code>
///   ★ 第 2 片第 1 级峰值 3860 °C，已超铂熔点 1768 °C —— 停止迭代。
///     这不是迭代不够：…局部发热物理上就下不来。⇒ 回 Rhino 把该级加厚 / 挪槽…
/// </code>
/// ⚠ 而那句「物理上就下不来」是**断言** —— 代码从来没真加厚试过一次。
///
/// ══ 现在
///
/// 过热 ⇒ 先**实测**加厚：加到工艺上界评一次。
///   · 凉了   ⇒ 二分求**最小**够用厚度（最小 = 最省铂）
///   · 仍不凉 ⇒ 这是**量出来的**「厚度救不了」⇒ 交给下一根旋钮：增宽（搜形状）
/// </summary>
public class OverheatDrivesSizingTests
{
    private static string Sizer() => File.ReadAllText(Path.Combine(
        HandoverDoc.Root(), "Pt_Optimize", "Core", "FlangeAutoSizer.cs"));

    /// <summary>★★★ 过热要真的去加厚 —— 而且是**绝对**加厚（乘在各级倍数上）。</summary>
    [Fact]
    public void 过热会驱动加厚()
    {
        string s = Sizer();
        Assert.Contains("hottestC > opt.OverheatRaiseFromC", s);
        Assert.Contains("先加厚这一级", s);
        // ★★★ 2026-09-03 用户更正：**局部**过热要加**局部**截面 ——
        //   只动过热的那一级，不是整片乘一个倍数。
        //   整片加厚等于替不热的级也花铂，违反「能用且铂最省」。
        Assert.Contains("if (!Locked(jh, mh)) probe[jh][mh] *= k;", s);
        Assert.DoesNotContain("for (int m = 0; m < probe[jh].Length; m++)", s);
    }

    /// <summary>
    /// ★★★ 「厚度救不了」必须是**实测**的：加到工艺上界评一次，仍不凉才这么说。
    /// 断言与实测的差别，正是这次修的东西。
    /// </summary>
    [Fact]
    public void 说厚度救不了之前要先加到上界试一次()
    {
        string s = Sizer();
        Assert.Contains("double hotHi = HotAt(hiK);", s);
        Assert.Contains("实测，不是估的", s);
        // 反面：不许再不试就断言「物理上就下不来」当作停机理由
        int a = s.IndexOf("double hotHi = HotAt(hiK);", StringComparison.Ordinal);
        Assert.True(a > 0);
    }

    /// <summary>
    /// ★★★ 加厚有用时要取**最小够用**的那一档 —— 「能用且铂最省」。
    /// ⚠ 2026-09-17 Opus 5（J 路，独立复核后改）：这一条原来钉的是生产那一行的**源码文本**
    /// （<c>if (HotAt(mid) &lt;= opt.OverheatRaiseFromC) hi = mid; else lo = mid;</c>）—— 又一道「手抄生产配方」的门：
    /// 复核查出二分那三步没有 NaN 闸（判不了当成「还过热」，出口照印「压住了」），要把二分提成公开函数
    /// <see cref="FlangeAutoSizer.MinRaiseFactor"/> 才好在里面加闸、也才验得了 —— 一提，这条当场红（本轮快套件首跑那 1 红就是它）。
    /// 改成**行为门 + 接线源码门**，门槛只高不低：取的仍必须是「最小够用的那一档」（够用、且不比最小够用的厚出一格），
    /// 另加一条原来没有的 —— **中间档算不出时不许给档**。
    /// </summary>
    [Fact]
    public void 加厚有用时取最小够用的那一档()
    {
        string s = Sizer();
        Assert.Contains("二分找**最小**够用的倍数（最省铂）", s);
        Assert.Contains("哪一级热就加哪一级", s);
        Assert.Contains("var rb = MinRaiseFactor(HotAt, hiK, opt.OverheatRaiseFromC);", s);   // 生产的二分就是这一份
        // 行为（调生产那一份）：阈值 1300 °C、工艺上界 ×2，凡 ≥ ×1.2 就凉 ⇒ 取到的那一档必须够用，且不比最小够用的厚出一格
        var r = FlangeAutoSizer.MinRaiseFactor(k => k >= 1.2 ? 1200.0 : 1400.0, 2.0, 1300.0);
        Assert.False(r.Undetermined);
        Assert.Equal(1.25, r.K, 12);
        Assert.True(r.K >= 1.2, $"取的那一档 ×{r.K:0.000} 根本不够用");
        Assert.True(r.K - 1.2 < 0.08, $"取到 ×{r.K:0.000}，比最小够用的 ×1.2 厚出一格以上 —— 白花铂");
        // 判不了不许给档（2026-09-17 复核查出的那条病）
        Assert.True(FlangeAutoSizer.MinRaiseFactor(_ => double.NaN, 2.0, 1300.0).Undetermined);
    }

    /// <summary>
    /// ★★★★ 加出来的厚度是**只增不减的下界** —— 外层按抽热误差削薄时不许削破它。
    /// 不守这条，这一轮为压熔化加的厚下一轮就被抹掉，两层互相打架，
    /// 而工程师看到的是「跑了很久、数不动」。
    /// </summary>
    [Fact]
    public void 加出来的厚度是只增不减的下界()
    {
        string s = Sizer();
        Assert.Contains("var overheatFloor =", s);
        Assert.Contains("scale[j][m] = Math.Max(scale[j][m] * kj, overheatFloor[j][m]);", s);
        Assert.Contains("Math.Max(overheatFloor[j][m],", s);   // 内层归一化那一处也守
    }

    /// <summary>
    /// ★★★ 熔点闸**不许覆盖**上面那句实测结论 —— 覆盖回去等于把量出来的证据
    /// 换成一句没量过的断言。
    /// </summary>
    [Fact]
    public void 熔点闸不覆盖实测结论()
    {
        Assert.Contains("if (last.Terminal && last.TerminalWhy.Length > 0)", Sizer());
    }

    /// <summary>
    /// ★★★★ 厚度走到头之后，**下一根旋钮是增宽** —— 指路必须转过去，
    /// 不是停在那里让工程师自己想办法。
    /// </summary>
    [Fact]
    public void 厚度走到头就转去增宽()
    {
        var st = new FlowState
        {
            Last = new LineResult
            {
                Ok = true,
                Converged = true,
                Checks = new[]
                {
                    new ConstraintOut
                    {
                        Name = LineResult.Key.FlangeDip, Kind = CheckKind.HardSafety,
                        Actual = 270.5, Limit = 10.0, Ok = false,
                    },
                },
            },
            SizerProvedInfeasible = true,          // = 厚度这根实测走到头
        };
        st.CurrentSnap = st.SolvedSnap = "同一个快照";

        var ns = Flow.Next(st, c => c is "shape.search" or "core.autoThick" or "core.runLine");
        Assert.NotNull(ns);
        Assert.Equal("shape.search", ns!.CmdId);
        Assert.Contains("走到头", ns.Why);
    }

    /// <summary>
    /// ★★★★ **试探的成本要钉住**（2026-09-03，第一版当场超时）。
    ///
    /// 第一版每次试探是一次**完整整线解**（分钟级）：
    /// <code>
    ///   探上界 1 次 + 二分 8 步 = 9 次全解／轮 × 最多 6 轮 = 多出 54 次全解
    /// </code>
    /// ⇒ 自动定厚在 60 分钟预算内跑不完（deliverable/F_改后_加厚试探超时.txt）。
    /// **逻辑对、实现太贵** —— 而「贵」在这个项目里等于「工程师用不了」。
    ///
    /// 三样一起改：粗网格试探 / 二分 4 步 / 每次求解只试一轮。
    /// 这条门盯着它们别被「顺手」改回去 —— 改回去的表现是**静默超时**，
    /// 不报错、不红，只是永远出不来结果。
    /// </summary>
    [Fact]
    public void 试探的成本被钉住了()
    {
        string s = Sizer();

        // ① 试探走粗网格，但**只放粗平坦区** —— 焊缝环只有 leg ~1-2.5 mm 宽，
        //    把细网格也粗化会判错「哪一级最热」，那是加厚加到错的级上（用户点出）。
        Assert.Contains("inner: null, coarse: true", s);
        Assert.Contains("lc.MeshCoarseMm = Math.Max(lc.MeshCoarseMm, 20.0);", s);
        Assert.DoesNotContain("lc.MeshFineMm = Math.Max", s);
        Assert.DoesNotContain("lc.MeshFineRadiusMm = Math.Min", s);

        // ② 二分 4 步（8 步的精度远细于图纸 0.01 mm 的格，白花四次解）
        Assert.Contains("for (int it = 0; it < 3 && hi - lo > 0.08; it++)", s);
        Assert.DoesNotContain("for (int it = 0; it < 8", s);
        // ★ 复核只在「差一点」时做 —— 差得远的粗细网格都救不回来，
        //   多花一次整线解只是把超时买回来（实测 F_改后4.txt）。
        Assert.Contains("bool nearMiss =", s);

        // ③ 试探按（片,级）记**次数**，不是 bool。
        //    全局 bool 的后果实测过：第 1 轮加厚成功 continue，第 2 轮被跳过，
        //    仍然过热时落回老熔点闸判死刑 —— 省成本的那一刀把功能本身砍掉了。
        Assert.Contains("var raiseTries =", s);
        Assert.Contains("raiseTries[hottestPlate][hottestLevel] < MaxRaiseTries", s);
        Assert.DoesNotContain("raisedOnce", s);
        Assert.DoesNotContain("raisedAt", s);

        // ④ 否定分支要在**原网格**复核一次再下结论 —— 只在否定时多花这一次解
        Assert.Contains("原网格复核", s);
        Assert.Contains("inner: null, coarse: false", s);
    }

    /// <summary>
    /// ★★★★★ **每一条「厚度救不了」的出口都必须是量出来的**（2026-09-03）。
    ///
    /// 熔点闸原文是一句**没量过**的断言：
    ///   「该级太薄、电流被挤在窄带上，局部发热物理上就下不来 ⇒ 回 Rhino…」
    /// 加了实测试探之后，它仍然从**两条出口**漏出来（实测 F_改后3.txt 第 3 步）：
    ///   · `hiK <= 1`（已在工艺上界）只报了句进度，**没设 Terminal** ⇒ 落回老文案
    ///   · 试探次数用尽 ⇒ 同样落回老文案
    /// 两条都是「其实量过了，却把没量过的话印出来」。
    ///
    /// ⇒ 本门钉：这三条出口都要设 Terminal，并且 TerminalWhy 里带**实测温度**。
    /// </summary>
    [Fact]
    public void 每条出口都带实测结论()
    {
        string s = Sizer();

        // 三条出口：加不动 / 加了仍不凉 / 次数用尽
        Assert.Contains("加厚这一级已经加不动了", s);
        Assert.Contains("加厚这一级救不了它", s);
        Assert.Contains("已试加厚 ", s);

        // 每一条都要真的置位，而不是只报一句进度
        Assert.True(s.Split("last.Terminal = true").Length - 1 >= 3,
            "「厚度救不了」的出口少于三条置位 —— 漏掉的那条会把没量过的断言印给工程师");

        // ★ 老那句没量过的断言**仍然合法地留着当兜底**（试探没触发时熔点闸照旧要说话）。
        //   真正的不变量不是「它不许存在」，而是「**量过了就不许印它**」——
        //   即熔点闸必须让 Terminal 的实测结论**覆盖**掉它。
        //   ⚠ 我先前把门写成「代码里不许出现这句话」，两次假红：
        //     一次扫到注释、一次扫到这条合法兜底。**门要钉不变量，不要钉字面。**
        Assert.Contains("if (last.Terminal && last.TerminalWhy.Length > 0)", s);
        int ov = s.IndexOf("if (last.Terminal && last.TerminalWhy.Length > 0)", StringComparison.Ordinal);
        int old = s.IndexOf("局部发热物理上就下不来", ov, StringComparison.Ordinal);
        Assert.True(old > ov, "实测结论没有排在老断言前面 —— 覆盖不到就等于没覆盖");
    }

    /// <summary>★ 自证：触发点取的是铂熔点，不是随手挑的一个数。</summary>
    [Fact]
    public void 自证_触发点是铂熔点()
    {
        var o = new FlangeAutoSizer.Options();
        Assert.Equal(Materials.PtMeltC, o.OverheatRaiseFromC);
    }
}
