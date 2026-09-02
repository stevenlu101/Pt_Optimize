using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **段数是可调的**（2026-09-02 用户点出的 bug）。
///
/// 用户实测：段表加到 HC4（4 段）之后，界面上的逐片输入**仍然只有 4 片**。
/// 而核心一直是 n 段的（<c>SegmentCount => SetpointC.Length</c>、<c>FlangeCount = n+1</c>）
/// ⇒ 4 段该有 5 片。界面少一片 ⇒ **算的是另一个零件**。
///
/// 病灶分三层，都写死 4：
/// <code>
///   DesignSpec      逐片数组是四元素字面量
///   DesignSpecStore NeedA(..., 4) —— 4 段的档会被当场拒
///   LineDesignPage  for (int i = 0; i &lt; 4; i++) —— 22 处
/// </code>
/// 用户：「UI 段数是必须可调整的」。
/// </summary>
public class SegmentCountTests
{
    /// <summary>★ 片数只有一个口径：段数 + 1。</summary>
    [Fact]
    public void 片数等于段数加一()
    {
        var d = new DesignSpec { SetpointC = new[] { 1150.0, 1080.0, 1050.0, 1000.0 } };
        Assert.Equal(5, d.FlangeCount);
        Assert.Equal(LineSolver.FlangeCount(4), d.FlangeCount);   // 与核心同一个口径
    }

    /// <summary>
    /// ★★★ <see cref="DesignSpec.Fit"/> 在**倒数第二个**位置增删 —— 保住「首=入口、末=出口」。
    ///
    /// 往末尾加会把出口片挤成共用片，而出口片的厚度/保温与共用片差着一倍以上
    /// （W08 实测 0.89 / 2.45 / 2.35 / 0.73）⇒ 那是**静默换零件**。
    /// </summary>
    [Fact]
    public void 加一段时新的那片插在出口之前()
    {
        var d = new DesignSpec
        {
            SetpointC = new[] { 1150.0, 1080.0, 1050.0, 1000.0 },     // 4 段 ⇒ 5 片
            TabThickMm = new[] { 0.89, 2.45, 2.35, 0.73 },            // 还是 4 片
        };
        d.Fit();
        Assert.Equal(5, d.TabThickMm.Length);
        Assert.Equal(0.89, d.TabThickMm[0]);        // 入口没动
        Assert.Equal(0.73, d.TabThickMm[^1]);       // 出口还在最后
        Assert.Equal(2.35, d.TabThickMm[3]);        // 新的那片沿用上一片共用片
    }

    /// <summary>★ 减一段：从倒数第二个位置删，出口仍是出口。</summary>
    [Fact]
    public void 减一段时删的是共用片不是出口()
    {
        var d = new DesignSpec
        {
            SetpointC = new[] { 1150.0, 1080.0 },                     // 2 段 ⇒ 3 片
            TabThickMm = new[] { 0.89, 2.45, 2.35, 0.73 },
        };
        d.Fit();
        Assert.Equal(3, d.TabThickMm.Length);
        Assert.Equal(new[] { 0.89, 2.45, 0.73 }, d.TabThickMm);
    }

    /// <summary>
    /// ★★★★★ **真造一个页面，加一段，问它逐片输入有没有跟上**。
    ///
    /// 这一条是用户那张截图的自动化版本 —— 上面那几条只验 Core，
    /// 而 bug 是「界面没跟上」，只有真造页面才问得出来。
    /// </summary>
    [Fact]
    public void 段表加一行之后界面真的多出一片()
    {
        var page = new LineDesignPage(new DesignInputs());
        NumericUpDown[] Plates() => (NumericUpDown[])typeof(LineDesignPage)
            .GetField("_tPlate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;

        int before = Plates().Length;
        Assert.True(before >= 3, $"开箱就该有片（实得 {before}）—— 否则下面是空转");

        // 走**页面自己的**段表（不是直接改字段）——测的是「工程师加一行」这条路
        var segs = (System.Collections.IList)typeof(LineDesignPage)
            .GetField("_segs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        int nSeg = segs.Count;
        var rowType = segs[0]!.GetType();
        var row = System.Activator.CreateInstance(rowType)!;
        rowType.GetProperty("名称")!.SetValue(row, "HC" + (nSeg + 1));
        rowType.GetProperty("控温C")!.SetValue(row, 1000.0);
        segs.Add(row);

        typeof(LineDesignPage).GetMethod("RebuildPlateRows",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);

        Assert.Equal(before + 1, Plates().Length);
        // 其余五组也要一起跟上 —— 只跟一组就是「算的还是另一个零件」
        foreach (string f in new[] { "_tabIns", "_ringMul", "_ringR1", "_ringR2", "_ringT2" })
        {
            var arr = (NumericUpDown[])typeof(LineDesignPage)
                .GetField(f, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
            Assert.True(arr.Length == before + 1, $"{f} 只有 {arr.Length} 个，该有 {before + 1} 个");
        }
    }

    /// <summary>
    /// ★ 段表的三个事件都要接上重建。只挂改名与删行的话，**「加一段」正好漏掉** ——
    /// 而那正是用户撞上的那一个。
    /// </summary>
    [Fact]
    public void 段表的加行事件也接上了重建()
    {
        string s = System.IO.File.ReadAllText(System.IO.Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
        Assert.Contains("_segGrid.RowsAdded += (_, _) => SegsChanged();", s);
        Assert.Contains("_segGrid.RowsRemoved += (_, _) => SegsChanged();", s);
        Assert.Contains("_segGrid.CellValueChanged += (_, _) => SegsChanged();", s);
        // 重建靠 ControlAdded 自动接上自动重算 —— 那条依赖不许消失
        Assert.Contains("c.ControlAdded += (_, e) => Watch(e.Control);", s);
    }
}
