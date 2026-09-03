using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **输出框里最该读的那一行，正是读不到的那一行**（2026-09-03 抓图抓到）。
///
/// 输出框 <c>WordWrap = false</c>（表格靠制表位对齐，折行会把列打散），
/// 而「【下一步】…」那条处置建议实测 <b>703 个字宽</b>，框只有 ~170 ⇒
/// 横向滚动条滑块只占 1/4：工程师要向右拉 500 个字才读得完程序给他的建议。
///
/// ⇒ <c>TextFmt.WrapProse</c> 只折**没有制表符**的行，表格原样放过。
///
/// ⚠ 折行必须认得 <c>**粗体**</c>：<c>AppendMarkup</c> 是**逐行**解析的，
///   把一对 <c>**</c> 对切开，两半各留一个孤立标记 ⇒ 屏幕上直接露出星号。
///   这条门就是钉这个：断行处要么不在粗体里，要么两边各自闭合。
/// </summary>
public class ProseWrapTests
{
    private static string[] Wrap(RichTextBox box, params string[] lines) =>
        (string[])typeof(Flow).Assembly.GetType("PtOptimize.UI.TextFmt")!
            .GetMethod("WrapProse", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { box, lines })!;

    /// <summary>造一个有真实宽度的输出框（句柄建出来，ClientSize 才是真的）。</summary>
    private static RichTextBox Box(int width = 600)
    {
        var f = new Form { Width = width + 40, Height = 300 };
        var b = new RichTextBox { WordWrap = false, Width = width, Height = 200 };
        f.Controls.Add(b);
        f.CreateControl();
        b.CreateControl();
        b.Width = width;
        return b;
    }

    [Fact]
    public void 长正文行会被折开()
    {
        var box = Box();
        string longLine = "　【下一步】" + string.Concat(Enumerable.Repeat("要让法兰变热才会少抽，", 40));
        var outp = Wrap(box, longLine);
        Assert.True(outp.Length > 1, $"703 字宽的一行没被折开（仍是 {outp.Length} 行）");
        // 每一行都该短得多
        Assert.All(outp, l => Assert.True(l.Length < longLine.Length / 2, "折出来的行还是太长"));
    }

    [Fact]
    public void 表格行一个字都不动()
    {
        var box = Box();
        string row = "类别\t判据\t实际\t限值\t裕度\t判定\t位置" + new string('长', 200);
        var outp = Wrap(box, row);
        Assert.Single(outp);
        Assert.Equal(row, outp[0]);      // 逐字未变 —— 折了它列宽就散
    }

    /// <summary>★★★ 断在粗体中间时，两半各自闭合，屏幕上不会露出孤立的 `**`。</summary>
    [Fact]
    public void 折行不把粗体对切开()
    {
        var box = Box();
        string line = "开头 **" + string.Concat(Enumerable.Repeat("这一段整个都是粗体的很长一句话，", 30)) + "** 结尾";
        var outp = Wrap(box, line);
        Assert.True(outp.Length > 1, "没折开 —— 下面的断言就是空转");
        foreach (var l in outp)
            Assert.True(CountMarks(l) % 2 == 0,
                $"这一行的 ** 是奇数个（孤立标记会原样印在屏幕上）：{l}");
    }

    private static int CountMarks(string s)
    {
        int n = 0;
        for (int i = 0; i + 1 < s.Length; )
            if (s[i] == '*' && s[i + 1] == '*') { n++; i += 2; } else i++;
        return n;
    }

    /// <summary>★★ 短行不许被动（别把「折行」修成「所有行都重排」）。</summary>
    [Fact]
    public void 短行原样放过()
    {
        var box = Box();
        var outp = Wrap(box, "短的一行", "", "也短");
        Assert.Equal(new[] { "短的一行", "", "也短" }, outp);
    }

    /// <summary>★★ 续行跟着首行缩进 —— 否则折出来的第二行会顶到最左边，读起来像新的一段。</summary>
    [Fact]
    public void 续行保留缩进()
    {
        var box = Box();
        string line = "　　" + string.Concat(Enumerable.Repeat("这是一段需要折行的长正文，", 30));
        var outp = Wrap(box, line);
        Assert.True(outp.Length > 1);
        Assert.All(outp, l => Assert.StartsWith("　　", l));
    }

    /// <summary>
    /// ★★★★ **直接给 `.Text` 赋值的内容不许被吞掉**（2026-09-03，我自己踩的）。
    ///
    /// 折行需要「没折过的原文」，于是我加了一份 <c>_rawText</c> 缓存。
    /// 但 <c>Reformat</c>（TextChanged 的处理器）上一版**优先读那份缓存** ⇒
    /// 谁直接写 <c>box.Text = 新内容</c>，就会被上一次 Write 存的**旧文整段盖掉**。
    ///
    /// 被吞掉的正是这一条：
    /// <code>
    ///   _out.Text = "⚠ 参数表改了「控温点 HC1」—— 上一次的解不再对应当前参数…" + _out.Text;
    /// </code>
    /// 也就是「你改了参数，上一次的结论作废了」这句**必须送到**的警告。
    /// 「把话说了却没送到」是本项目最贵的一类错。
    /// </summary>
    [Fact]
    public void 直接赋值的文字不会被旧内容盖掉()
    {
        var box = Box();
        var fmt = typeof(Flow).Assembly.GetType("PtOptimize.UI.TextFmt")!;
        void Write(string t) => fmt.GetMethod("Write",
            BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { box, t, false });

        Write("第一次写进去的内容");
        // ⚠ 不写反斜杠转义：本仓的钩子会把它改成真的换行，字面量当场断掉
        box.Text = "⚠ 参数表改了「控温点 HC1」" + Environment.NewLine + box.Text;   // 绕过 Write，直接赋值
        Assert.Contains("控温点 HC1", box.Text);
    }

    /// <summary>★ 自证：框窄到量不出来时**什么都不做**，而不是把每个字折一行。</summary>
    [Fact]
    public void 自证_量不出宽度时不折()
    {
        var box = new RichTextBox { Width = 10 };     // 没建句柄
        string line = string.Concat(Enumerable.Repeat("很长很长", 100));
        Assert.Equal(new[] { line }, Wrap(box, line));
    }
}
