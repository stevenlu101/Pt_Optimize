using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// σ(T) 电流场耦合 —— **机制造好了没接线**，但**实测无影响**。两件事都要说清。
///
/// ★ 病灶（2026-08-28 第一性原理通查，主进程复验）：
///   <c>ShellCurrent.Solve</c> 的第五个形参 <c>tempC</c> 就是为 σ(T) 造的
///   （<c>sig[i] = ρe(T_ref)/ρe(T_i)</c>，写对了），
///   但**全仓 12 个调用点没有一个传过它** ⇒ 交付用的整线链恒按**等温**解电流场。
///   同一片法兰上管孔 1150 °C、压接段 450 °C、舌片可超 1400 °C，
///   ρe(450)/ρe(1150) ≈ 0.45 ⇒ 冷区更导电、电流往那头挤，等温模型看不见 ——
///   而 ②″ 判的正是局部电流拥塞造成的峰值。
///
/// ★ 项目**做对过**：已被取代的 <c>CoupledSolver</c> 每轮都用新温度场重解电流场，
///   注释写着「铂 700–1300 °C 间 ρe 变化 48 %…等温 σ 会算偏 J 分布」。
///   重写成 ShellThermal/LineRunner 这条路时**丢掉了**。
///
/// **实测**（--window --wall 0.8，默认六个板厚标度，等温 vs σ(T)）：
/// <code>
///   标度   0.30   0.50   0.70   1.00   1.40   2.00
///   ②″    −0.66  −0.44  −0.27  −0.16  −0.10  −0.05   ← 两边**逐位相同**
///   管J     7.60   7.67   7.68   7.70   7.72   7.74   ← 两边**逐位相同**
///   ③ 差 ≤ 0.1 K
/// </code>
/// ⇒ **缺陷是真的（机制没接线），影响是零（量出来的，不是推断的）。**
///   故**保持默认关**：它每片多两次求解，换不来任何变化。开关留着，结论用数据封口。
/// </summary>
public class SigmaOfTTests
{
    [Fact]
    public void 默认关_它每片多两次求解却换不来变化()
    {
        Assert.False(new DesignInputs().SigmaOfTCoupling);
    }

    /// <summary>接线必须真的在（否则这条「实测无影响」是在验一个没跑过的东西）。</summary>
    [Fact]
    public void 接线真的在_而且传的是温度场()
    {
        string s = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "LineRunner.cs"));
        Assert.Contains("c.Base.SigmaOfTCoupling", s);
        Assert.Contains("tempC: th.T", s);          // ★ 真的把温度场传进去了
    }

    /// <summary>σ(T) 的机制本身在 ShellCurrent 里，且默认不启用 —— 钉住现状。</summary>
    [Fact]
    public void 机制在ShellCurrent里_形参默认null()
    {
        string s = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "ShellCurrent.cs"));
        Assert.Contains("double[]? tempC = null", s);
        Assert.Contains("tempC == null ? 1.0", s);  // null ⇒ 等温（σ 恒为 1）
    }

    /// <summary>
    /// 冷区确实更导电 —— 这是「电流会往那头挤」的物理前提。
    /// 前提不成立的话，上面整条推理都不用谈了。
    /// </summary>
    [Fact]
    public void 自证_铂在低温下电阻率确实低得多()
    {
        double r450 = Materials.PtResistivity(450), r1150 = Materials.PtResistivity(1150);
        Assert.True(r450 < r1150, "低温电阻率必须更低");
        double ratio = r450 / r1150;
        Assert.True(ratio < 0.6, $"450 °C 与 1150 °C 的电阻率比应显著小于 1，实测 {ratio:0.00}");
    }
}
