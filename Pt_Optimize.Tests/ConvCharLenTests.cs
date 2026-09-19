using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 对流特征长度 —— **承重且没有依据**的一个数。
///
/// ★ 它是 Churchill–Chu 自然对流与平板强制对流**唯一的几何输入**。
///   此前写死 <c>0.05</c> 且**四处各存一份**
///   （ShellThermal、DesignScreen×2、RampTwoNode），全都与几何脱钩 ——
///   而盘径与舌长**正是被优化的变量**：形状在变，决定它散热系数的那个长度不动。
///
/// ★★ **实测灵敏度极高**（2026-08-28）：换成网格包围盒跨度（约 0.17 m）后 ——
/// <code>
///   设计记录 0.8 档  ②′  +1.123 W → **−1.880 W**   （负 = 热往管里灌，烧断方向）
///                ③   +5.182 K → −0.643 K
///   设计记录 0.6 档  ②′  +0.820 W → −1.164 W
///   ⇒ 两个设计记录双双「**设计记录自己不过判据**」
/// </code>
///
/// ⚠ **但 0.17 同样是猜的**：Churchill–Chu 要的是**竖直板高度**，
///   而这片板在现场怎么摆没有确认过（舌片朝下 ~170 mm；盘立舌横 ~60 mm）。
///   **把一个拍的数换成另一个拍的数、并借此翻掉设计记录，那不叫修复。**
///
/// ⇒ 处置：升为**显式输入**（带出处、标「待现场确认」），默认仍取 0.05
///   —— 那是**保持现状**，不是有依据。四处收敛到这一个来源。
/// </summary>
public class ConvCharLenTests
{
    [Fact]
    public void 默认值保持现状_不擅自改动设计记录的口径()
    {
        Assert.Equal(0.05, new DesignInputs().ConvCharLenM, 9);
    }

    [Fact]
    public void 四处各存一份已经收敛到唯一来源()
    {
        string root = HandoverDoc.Root();
        foreach (var f in new[] { "Pt_Optimize/Core/ShellThermal.cs",
                                  "Pt_Optimize/Core/DesignScreen.cs" })
        {
            string s = File.ReadAllText(Path.Combine(root, f));
            Assert.DoesNotContain("double charLen = 0.05;", s);   // 不许再各存一份
            Assert.Contains("p.ConvCharLenM", s);                 // 都从同一处取
        }
        // ★ R48（2026-09-14，Opus 5）有意改断言：RampTwoNode 原断言「文件里含 p.ConvCharLenM」→ 新断言「法兰表面热流经唯一配方 DesignScreen.PlateFluxWPerM2」
        //   （那份配方自己读 p.ConvCharLenM，上面钉着）；PlateThermal2D 原写死 `double charLen = 0.05;`，同批改走唯一配方，一并钉进来。
        //   原因：审查意见「圆盘保温 0 mm 时四个消费方物理含义不一致」的修法是法兰表面热流只留一份配方，这两个文件不再自己持有特征长度。
        //   依据文件：Pt_Optimize/Core/RampTwoNode.cs、Pt_Optimize/Core/PlateThermal2D.cs、Pt_Optimize/Core/DesignScreen.cs。
        foreach (var f in new[] { "Pt_Optimize/Core/RampTwoNode.cs", "Pt_Optimize/Core/PlateThermal2D.cs" })
        {
            string s = File.ReadAllText(Path.Combine(root, f));
            Assert.DoesNotContain("charLen = 0.05", s);
            Assert.Contains("DesignScreen.PlateFluxWPerM2(", s);
        }
    }

    /// <summary>
    /// 自然对流对 L **不是**完全不敏感 —— 这是「它承重」的物理根据。
    /// 高 Ra 下 s² ∝ l ⇒ h = s²k/l 与 l 无关；但 s = 0.825 + 0.387·Ra^(1/6)/den
    /// 里那个常数项在中等 Ra 下占可观比例，于是 h 仍随 l 变。
    /// </summary>
    [Fact]
    public void 自然对流的h确实随特征长度变_所以这个数是承重的()
    {
        double h05 = Materials.HConvVertical(900, 25, 0.05);
        double h17 = Materials.HConvVertical(900, 25, 0.17);
        Assert.True(h05 > 0 && h17 > 0);
        double rel = System.Math.Abs(h05 - h17) / h05;
        Assert.True(rel > 0.05, $"h 对 L 的相对变化应可观，实测 {rel:P1}");
    }

    /// <summary>
    /// 强制对流那一支 h ∝ L^(−1/2)，**强相关**。默认吹风 = 0 所以不激活；
    /// 一旦现场吹风，这个数就直接咬。自证：拿 50 mm 代 165 mm，h 高估近 1.8 倍。
    /// </summary>
    [Fact]
    public void 强制对流下它是强相关的_现场一吹风就咬()
    {
        Assert.Equal(0.0, new DesignInputs().FlangeAirVelocityMPerS, 9);   // 默认不吹
        double h05 = Materials.HConvForcedPlate(900, 25, 0.05, 5.0);
        double h165 = Materials.HConvForcedPlate(900, 25, 0.165, 5.0);
        Assert.True(h05 / h165 > 1.5, $"h(0.05)/h(0.165) 应显著 >1，实测 {h05 / h165:0.00}");
    }
}
