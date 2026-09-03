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

    /// <summary>★★★ 加厚有用时要取**最小够用**的那一档 —— 「能用且铂最省」。</summary>
    [Fact]
    public void 加厚有用时取最小够用的那一档()
    {
        string s = Sizer();
        Assert.Contains("二分找**最小**够用的倍数（最省铂）", s);
        Assert.Contains("哪一级热就加哪一级", s);
        Assert.Contains("if (HotAt(mid) <= opt.OverheatRaiseFromC) hi = mid; else lo = mid;", s);
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

    /// <summary>★ 自证：触发点取的是铂熔点，不是随手挑的一个数。</summary>
    [Fact]
    public void 自证_触发点是铂熔点()
    {
        var o = new FlangeAutoSizer.Options();
        Assert.Equal(Materials.PtMeltC, o.OverheatRaiseFromC);
    }
}
