using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **每段直接加热铂金管的长度，可以单独设定**（用户 2026-09-03）。
///
/// 用户原话：「每段直接加热铂金管的长度必须是可以单独设定的」、
/// 「每段长度变数在 UI 的名称『直接加热铂金管的长度』」。
///
/// 原来它是**一个标量**（<c>LineCase.SegLengthMm</c>，取自参数表的「段长 L」），
/// 所有段共用 ⇒ 现场三段加热长度不同时，一个数按不住，而报告照样出。
///
/// ══ 同一轮还钉住「段数牵动了什么」
///
/// 用户 2026-09-03：「不论是 UI 或是 3DM 输入，需几段加热都由 UI 输入框输入」，
/// 以及「需几段加热这个变数所牵动的关联参数重新检查一遍」。
/// 2026-09-02 那次「段数可调」**只修了解析路**：图纸路的文件行、导出循环、
/// <c>DesignSpec.BuildCase</c> 里字面枚举的 <c>Plate(0..3)</c> 全是写死 4 的。
/// </summary>
public class SegLengthPerSegmentTests
{
    /// <summary>★★★ 逐段长度真的**逐段生效**：改一段的长度，只有那一段的铂重变。</summary>
    [Fact]
    public void 每段长度各算各的()
    {
        var d = new DesignSpec
        {
            SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
            SegLengthMm = new[] { 200.0, 300.0, 400.0 },
        };
        var c = d.BuildCase(new DesignInputs(), checkRamp: false);
        Assert.Equal(new[] { 200.0, 300.0, 400.0 }, c.SegLengthMm);
    }

    /// <summary>
    /// ★★★ 没给（或给少了）由 <c>LineRunner.Normalize</c> **一处**兜底 —— 别处不许补。
    /// 兜底只有一个来源，否则「显示的」与「算的」会分家。
    /// </summary>
    [Fact]
    public void 没给长度时按参数表铺满()
    {
        var c = new LineCase
        {
            Base = new DesignInputs { TubeLengthMm = 275 },
            SetpointC = new[] { 1150.0, 1080.0, 1050.0, 1000.0 },
            SegLengthMm = Array.Empty<double>(),
        };
        typeof(LineRunner).GetMethod("Normalize",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { c });
        Assert.Equal(4, c.SegLengthMm.Length);
        Assert.All(c.SegLengthMm, v => Assert.Equal(275, v));
    }

    /// <summary>★★ 给了一半：给了的留着，没给的才铺默认。</summary>
    [Fact]
    public void 只给一部分时不覆盖已给的()
    {
        var c = new LineCase
        {
            Base = new DesignInputs { TubeLengthMm = 300 },
            SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
            SegLengthMm = new[] { 220.0 },
        };
        typeof(LineRunner).GetMethod("Normalize",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { c });
        Assert.Equal(new[] { 220.0, 300.0, 300.0 }, c.SegLengthMm);
    }

    /// <summary>★★★ 界面上就叫这个名字（用户指定）。</summary>
    [Fact]
    public void 界面上叫直接加热铂金管的长度()
    {
        var pi = typeof(LineDesignPage.SegRow).GetProperty("直接加热管长mm");
        Assert.NotNull(pi);
        var dn = pi!.GetCustomAttribute<System.ComponentModel.DisplayNameAttribute>();
        Assert.NotNull(dn);
        Assert.Contains("直接加热铂金管的长度", dn!.DisplayName);
    }

    /// <summary>
    /// ★★★★ **段数一变，逐片的东西全都要跟着变** —— 这条钉的是
    /// <see cref="DesignSpec.BuildCase"/> 里那两处**字面枚举**（Plate(0..3) / 四个 ClampTempC）。
    /// 扫「循环 &lt; 4」抓不到它们，而分 4 段时第 5 片根本不进 LineCase：
    /// 判据表照样出数，**算的是另一个零件**。
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void 分几段就有几片(int nSeg)
    {
        var d = new DesignSpec { SetpointC = Enumerable.Repeat(1100.0, nSeg).ToArray() }.Fit();
        var c = d.BuildCase(new DesignInputs(), checkRamp: false);
        Assert.Equal(nSeg + 1, c.FlangePlates.Length);
        Assert.Equal(nSeg + 1, c.ClampTempC.Length);
        Assert.Equal(nSeg, c.SetpointC.Length);
        Assert.Equal(nSeg, d.SegLengthMm.Length);      // Fit 把逐段长度也调好了
    }

    /// <summary>★★ 存档往返不许丢逐段长度（存了复现不出来 = 白存）。</summary>
    [Fact]
    public void 存档往返留得住逐段长度()
    {
        // 从现役档 Clone 出来改 —— 必填字段（provenance 等）由它带全，
        // 不另手搭一份「刚好能过」的样本（那种样本会随格式演进悄悄过期）。
        var fd = DesignSpec.Builtin[0].Clone();
        fd.SetpointC = new[] { 1150.0, 1080.0, 1050.0 };
        fd.SegLengthMm = new[] { 210.0, 320.0, 430.0 };
        fd.Fit();

        fd.Name = "★往返测试★ 逐段长度 " + Guid.NewGuid().ToString("N")[..6];
        string? written = null;
        try
        {
            written = DesignSpecStore.Save(fd);
            // 与 DesignSpecStoreTests 走同一条读回路径（Parse），别另起一套
            var m = typeof(DesignSpecStore).GetMethod("Parse",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var back = (DesignSpec)m.Invoke(null,
                new object[] { File.ReadAllText(written), "test.fd.json" })!;
            Assert.Equal(fd.SegLengthMm, back.SegLengthMm);
        }
        finally { if (written is not null && File.Exists(written)) File.Delete(written); }
    }

    /// <summary>
    /// ★★★ **图纸路的文件行也按段数走**（用户：「不论是 UI 或是 3DM 输入，
    /// 需几段加热都由 UI 输入框输入」）。原来 <c>_file3dm</c> 是定长 4 的字段。
    /// </summary>
    [Fact]
    public void 图纸文件行数跟着段数走()
    {
        string src = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
        // 反面：不许再写死四个
        Assert.DoesNotContain("_file3dm = { new(), new(), new(), new() }", src);
        Assert.DoesNotContain("_row3dm = new Control[4]", src);
        // 正面：段数一变就重建（与逐片输入同一个入口）
        Assert.Contains("RebuildFile3dmRows();", src);
    }

    /// <summary>
    /// ★★★★ **段表上真的多出那一列，且列头就是用户指定的名字**。
    ///
    /// 上一条查的是 <c>[DisplayName]</c> 这个标注在不在；这一条查
    /// <b>DataGridView 绑出来之后列头写的是什么</b> —— 标注写对了、
    /// 绑定没把它用上（AutoGenerateColumns 走的是 TypeDescriptor），屏幕上就还是属性名。
    ///
    /// ⚠ 段表在输入栏底部、抓图时被滚动挡住 ⇒ <c>--uishot</c> 看不到它。
    ///   这条门就是那张抓不到的图的替代：**真造一个页面，问它列头**。
    /// </summary>
    [Fact]
    public void 段表上真的有这一列且列头是这个名字()
    {
        // ⚠ 必须放进 Form 并建句柄：DataGridView 要有句柄才会按数据源绑出列，
        //   光 new 一个页面去问，列是空的（第一版就是这么假红的）。
        var page = new LineDesignPage(new DesignInputs());
        // ⚠ LineDesignPage 是 TabPage ⇒ 只能挂在 TabControl 上，不能直接进 Form。
        var tabs = new System.Windows.Forms.TabControl { Dock = System.Windows.Forms.DockStyle.Fill };
        tabs.TabPages.Add(page);
        var form = new System.Windows.Forms.Form { Width = 900, Height = 700 };
        form.Controls.Add(tabs);
        form.CreateControl();
        var grid = (System.Windows.Forms.DataGridView)typeof(LineDesignPage)
            .GetField("_segGrid", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(page)!;
        _ = grid.Handle;
        System.Windows.Forms.Application.DoEvents();

        var heads = grid.Columns.Cast<System.Windows.Forms.DataGridViewColumn>()
                        .Select(c => c.HeaderText).ToArray();
        Assert.True(heads.Length >= 4,
            "段表列数不足 —— 实得：" + string.Join(" / ", heads));
        Assert.Contains(heads, h => h.Contains("直接加热铂金管的长度", StringComparison.Ordinal));
        // 同一张表上另外两个共用输入也要在（段数/长度/控温点是共用输入群）
        Assert.Contains(heads, h => h.Contains("控温点", StringComparison.Ordinal));
    }

    /// <summary>★ 自证：段数确实只有一个口径（控温点的个数）。</summary>
    [Fact]
    public void 自证_段数只有一个口径()
    {
        var d = new DesignSpec { SetpointC = new[] { 1.0, 2.0, 3.0, 4.0 } };
        Assert.Equal(4, d.SegmentCount);
        Assert.Equal(5, d.FlangeCount);
    }
}
