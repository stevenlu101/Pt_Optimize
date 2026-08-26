using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 几何子进程必须**有能力报告失败**。
///
/// ★ 真事（2026-08-25）：Pt_Optimize.Geom 的六个模式都以
///     finally { Console.Out.Flush(); Environment.Exit(Environment.ExitCode); }
///   收尾，而 <c>Environment.ExitCode</c> 是**另一个属性**，默认恒为 0 ——
///   `return 4` 不会写进它。于是那一句等价于 Environment.Exit(0)，
///   把精心区分的失败码（4 = 图层无实体、2 = 异常）**全部抹成成功**。
///   ⇒ 这个子进程根本没有能力向主程序报告失败，而主程序每一处
///   `if (proc.ExitCode != 0)` 检查的都是一个恒为 0 的东西。
///
///   现场表现：拿**定案自己的图纸**跑 thickness，stderr 明明写着「图层无实体：法兰」，
///   退出码却是 0、stdout 为空 ⇒ 主程序照旧往下走，崩在 System.Text.Json，
///   报「The input does not contain any JSON tokens」—— 与真因隔了三层。
///
/// ⚠ 本条只能靠**读源码**验：子进程是 net7、独立进程、要装 Rhino 才跑得起来，
///   进不了单测。同族的先例是角焊缝那条（Geom 那一份合不了，由体积对账守着）。
///   读源码的断言比不验强，但要写明它验的是「写法」不是「行为」。
/// </summary>
public class ProbeExitCodeTests
{
    private static string Src()
    {
        string p = Path.Combine(HandoverDoc.Root(), "Pt_Optimize.Geom", "Program.cs");
        if (!File.Exists(p))
            throw new FileNotFoundException("找不到 " + p + " —— 断言失去了对象，**不能算通过**");
        return File.ReadAllText(p);
    }

    private static string[] CodeLines() => Src()
        .Split('\n')
        .Select(l => l.Trim())
        .Where(l => !l.StartsWith("//"))      // /// 也以 // 开头，一并排除
        .ToArray();

    [Fact]
    public void 代码里不许再出现Environment_ExitCode()
    {
        var bad = CodeLines().Where(l => l.Contains("Environment.ExitCode", StringComparison.Ordinal)).ToArray();
        Assert.True(bad.Length == 0,
            "Environment.ExitCode 恒为 0，用它退出等于把所有失败码抹成成功。命中：" +
            string.Join(" | ", bad));
    }

    [Fact]
    public void 自证_注释里确实还留着这个反面教材()
    {
        // 若源码读错了/读空了，上一条会在空集上「通过」—— 那是本项目记过案的安静失败。
        Assert.Contains("Environment.ExitCode", Src());
    }

    [Fact]
    public void 每个模式都用带真返回码的Bye收尾()
    {
        string s = Src();
        int calls = s.Split("Bye(").Length - 1;
        // 六个模式 + 一处定义 + 注释若干；至少要有六次调用
        Assert.True(calls >= 6, $"Bye( 只出现 {calls} 次，六个模式应各有一次");
        Assert.Contains("Environment.Exit(rc)", s);
    }

    [Fact]
    public void 主程序不许只信退出码_空输出也算失败()
    {
        string p = Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Geometry3dm.cs");
        string s = File.ReadAllText(p);
        Assert.Contains("IsNullOrWhiteSpace(stdout)", s);
        // 而且要把子进程真正说的话带出来 —— 否则又是一句没有信息量的错误
        Assert.Contains("stderr", s);
    }
}
