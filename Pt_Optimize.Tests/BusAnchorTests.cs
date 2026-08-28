using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 铜排是**部分锚点** —— 把「理想热沉 vs 绝热」这个二选一换成一条串联热阻。
///
/// ★ 起因（2026-08-28）：局部热稳定的横向导热长度 L 取「到最近定温边界的距离」。
///   舌端在**定温**边界下是理想热沉；但在**热导**边界（--busg）下它不是。
///   此前两种写法都不对：
///    · 旧写法：不管是不是定温，一律拿舌端当锚点 ⇒ 裕度**虚高** 2.0×（偏乐观）
///    · 保守退路：没有定温锚点就当绝热 ⇒ 裕度**虚低** 0.7×（过度保守）
///
/// ★ 建模：串联热阻 R = L_几何/(k·A_截面) + 1/G ⇒ **L_等效 = L_几何 + k·A_截面/G**。
///   后一项是「把铜排热阻折算成多长的舌片」。
///
/// **实测**（--selfcheck --busg 40,21.8,300 --sink 60）：
/// <code>
///                            0.8 档   0.6 档
///   定温边界（旧上界）          2.0×     1.9×
///   **铜排=部分锚点（建模后）**  **1.9×** **1.8×**
///   无锚点（保守下界）          0.7×     0.7×
/// </code>
/// ⇒ 真值**贴着上界**：等效长度只有约 4 mm，铜排几乎就是理想热沉。
///   那个「0.7× / 热稳定成为唯一拦路者」是**保守退路造出来的假象**。
/// </summary>
public class BusAnchorTests
{
    /// <summary>
    /// 等效长度的量级：k·A/G。舌宽 60 × 舌厚 1 mm、G = 1.119 W/K
    /// ⇒ 约 4 mm —— 也就是说铜排的热阻只相当于 4 mm 舌片。
    /// 这条把「为什么真值贴着上界」钉住：不是巧合，是数就这么小。
    /// </summary>
    [Fact]
    public void 等效长度只有几毫米_所以铜排几乎是理想热沉()
    {
        double kPt = Materials.PtThermalK(1100);          // W/(m·K)
        double aCross = 60.0 * 1.0 * 1e-6;                // 舌宽 60 × 厚 1 mm ⇒ m²
        double g = BusbarSizing.CuK * (40.0 * 21.8 * 1e-6) / (300.0 * 1e-3);   // ≈1.119 W/K
        double extraMm = kPt * aCross / g * 1e3;
        Assert.InRange(extraMm, 2.0, 8.0);
        // 自证：换一根**细得多**的铜排，等效长度必须显著变长（否则这个公式是死的）
        double gThin = BusbarSizing.CuK * (40.0 * 2.0 * 1e-6) / (300.0 * 1e-3);
        Assert.True(kPt * aCross / gThin * 1e3 > extraMm * 5,
            "铜排细十倍，等效长度应长十倍量级");
    }

    [Fact]
    public void 定温边界下等效长度为零_不许无端加长()
    {
        // 定温是理想热沉，串联项 1/G 不存在 ⇒ 只能是几何距离本身。
        string s = File.ReadAllText(Path.Combine(HandoverDoc.Root(),
                       "Pt_Optimize", "Core", "ShellThermal.cs"));
        Assert.Contains("double busExtraMm = 0.0;", s);
        Assert.Contains("if (busG &&", s);              // 只有热导模式才加
    }

    [Fact]
    public void 锚点判定_定温或热导都算_自由端不算()
    {
        string s = File.ReadAllText(Path.Combine(HandoverDoc.Root(),
                       "Pt_Optimize", "Core", "ShellThermal.cs"));
        // 舌端自由（既非定温也非热导）时不许当锚点 —— 那是旧写法偏乐观的根源
        Assert.Contains("if (tabCell[i] && (isFixed[i] || busG))", s);
    }

    [Fact]
    public void 等效长度被记进结果_算了就要报得出来()
    {
        string s = File.ReadAllText(Path.Combine(HandoverDoc.Root(),
                       "Pt_Optimize", "Core", "ShellThermal.cs"));
        Assert.Contains("public double BusEquivLenMm;", s);
        Assert.Contains("res.BusEquivLenMm = busExtraMm;", s);
    }
}
