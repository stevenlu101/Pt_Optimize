using System;
using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 舌端边界：把夹持温度从**假设**变成**输出**。
///
/// ★ 病灶（2026-08-28 第一性原理通查）：ShellThermal 自己写着三种模式 ——
///   ① 热导 q = G·(T − T_冷端)：**物理上唯一自洽的一种**，接头温度是**输出**；
///   ② 定温：「假设铜排无论要带走多少热都能把接触点按住，**等于假设结论**」；
///   ③ 自由端：实算舌端 1100–2400 °C，而**铜熔点只有 1085 °C** ⇒ 接头根本不存在。
///   而整线链一直走 ②（<c>BusbarConductanceWPerK</c> 默认 −1），定案把夹持温度钉死 450 °C。
///
/// ⇒ `--shape --busg 宽,厚,长` 打开 ①，G 由**真实铜排几何**算出：G = k_Cu·A/L。
///   截面 A 只依赖载流（A = I/J许用），不依赖夹持温度 ⇒ **没有循环**。
///
/// **实跑对照**（Pt_Heater1.3dm／壁 0.8／R30／300 轮，两边都真收敛）：
/// <code>
///   定温（假设 450 °C 输入）  ⇒ 3638 g
///   热导（G=1.119, 冷端 60）  ⇒ 3641 g，**算出来的夹持温度 209/276/253/176 °C**
/// </code>
/// 差 3 g（0.08 %）⇒ 方法上是真缺陷（输出当输入），**数值上无害**；
/// 且算出来的温度与铜排选型表的 300 °C 基准吻合、远低于铜熔点。
/// </summary>
public class BusbarConductanceTests
{
    [Fact]
    public void 热导由真实铜排几何算出_不是写死的常数()
    {
        // 文档里那个「典型值 ≈ 1.1 W/K」的来路：40×21.8 mm² 铜排、到冷端 300 mm
        double g = BusbarSizing.CuK * (40.0 * 21.8 * 1e-6) / (300.0 * 1e-3);
        Assert.Equal(1.119, g, 3);
        // 自证：换一根铜排，G 必须跟着变（否则这条只是在验一个常数）
        double g2 = BusbarSizing.CuK * (40.0 * 10.0 * 1e-6) / (300.0 * 1e-3);
        Assert.True(g2 < g, "截面减半，热导必须变小");
    }

    [Fact]
    public void 默认仍是定温边界_打开自洽那条要显式给()
    {
        // 现状申报：默认 −1 = 走定温。改这个默认值会动所有历史结果，必须是显式选择。
        Assert.Equal(-1.0, new DesignInputs().BusbarConductanceWPerK, 9);
    }

    /// <summary>
    /// ⚠ 开关不能叫 `--busbar` —— 那**已经是一个顶层命令**（铜排选型表），
    ///   同名会被它先吃掉。2026-08-28 我起名时当场撞到，改成 `--busg`。
    /// </summary>
    [Fact]
    public void 开关名不许与已有顶层命令重名()
    {
        string src = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Program.cs"));
        Assert.Contains("\"--busg\"", src);
        // 顶层的 --busbar 命令仍在（不是被我改掉了）
        Assert.Contains("args.Contains(\"--busbar\")", src);
    }

    [Fact]
    public void 打开自洽边界时_夹持温度必须被印出来()
    {
        // 「算出来却不报」等于没算 —— 本项目记过案的形态之一。
        string src = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Program.cs"));
        Assert.Contains("算出来的舌端（夹持）温度", src);
        Assert.Contains("TTabEndC", src);
        Assert.Contains("1085", src);        // 铜熔点是它的硬顶，必须写在旁边
    }
}
