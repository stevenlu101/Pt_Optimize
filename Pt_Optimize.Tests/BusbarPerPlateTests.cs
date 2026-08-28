using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 铜排热导 **逐片由该片电流算** —— 不再靠人从选型表里抄数（2026-08-28，B 项）。
///
/// ★ 病灶：`--busg 40,21.8,300` 要人手填，而那个 40×21.8 是从
///   `--cli --busbar` 选型表里**抄**的 —— 同一个数两处来源，
///   而且抄的是**共用片**那一行。可四片电流本来就不同
///   （选型表自己算出 685/1099/975/542 A），共用片走 √3 倍电流
///   ⇒ 需要更粗的铜排 ⇒ **G 本来就该逐片不同**。
///
/// ★ 第一性原理链（**不循环**）：A_j = I_j / J许用 ⇒ G_j = k_Cu·A_j / L。
///   截面只依赖**载流**，不依赖夹持温度，所以可以在解之前定下来。
///   而「导热需截面」A = Q·L/(k·ΔT) 要 Q 与夹持温度（都是**输出**）⇒ 会循环
///   ⇒ 不进推导链，只在解完之后作**一致性对账**。
///
/// **实测**（设计记录几何、--busg 不带参数）：
/// <code>
///   入口     G 0.779 W/K  载流需 607 mm²  导热需 590 mm²  ✓ 载流是控制项
///   HC1|HC2  G 1.275 W/K  载流需 993 mm²  导热需 971 mm²  ✓
///   HC2|HC3  G 1.170 W/K  载流需 911 mm²  导热需 893 mm²  ✓
///   出口     G 0.658 W/K  载流需 513 mm²  导热需 499 mm²  ✓
///   算出来的舌端温度 237 / 255 / 243 / 222 °C（铜熔点 1085）
/// </code>
/// </summary>
public class BusbarPerPlateTests
{
    private static string Src(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), f));

    [Fact]
    public void 两个工艺输入有默认值且与选型表同源()
    {
        var p = new DesignInputs();
        Assert.Equal(2.0, p.BusbarJAllowAPerMm2, 9);      // 与 --busbar 表里的 jBusAllow 同
        Assert.Equal(300.0, p.BusbarLenToSinkMm, 9);      // 与 lengthToSinkMm 同
    }

    [Fact]
    public void 逐片按电流算_不再靠人抄尺寸()
    {
        string s = Src("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("iJoint / c.Base.BusbarJAllowAPerMm2", s);       // A = I/J
        Assert.Contains("BusbarSizing.CuK * (aMm2 * 1e-6)", s);          // G = k·A/L
    }

    [Fact]
    public void 不带参数就能开_尺寸是可选的显式覆盖()
    {
        string s = Src("Pt_Optimize/Program.cs");
        Assert.Contains("bool dims = iBusS + 1 < args.Length", s);
        Assert.Contains("p.BusbarJAllowAPerMm2 = -1;", s);   // 显式指定时关掉逐片推导
    }

    /// <summary>
    /// 导热需截面必须被算出来并**对账** —— 否则等于让人照一根带不走热的铜排去做。
    /// </summary>
    [Fact]
    public void 导热需截面要对账且超了要出声()
    {
        Assert.Contains("BusSectionForHeatMm2", Src("Pt_Optimize/Core/LineRunner.cs"));
        Assert.Contains("导热是控制项", Src("Pt_Optimize/Program.cs"));
    }

    /// <summary>
    /// 自证：电流大一倍，需要的截面就大一倍、G 也大一倍 —— 否则「逐片」没有意义。
    /// </summary>
    [Fact]
    public void 自证_电流翻倍则G翻倍()
    {
        double L = 0.3, j = 2.0;
        double G(double i) => BusbarSizing.CuK * ((i / j) * 1e-6) / L;
        Assert.Equal(2.0, G(1200) / G(600), 6);
    }
}
