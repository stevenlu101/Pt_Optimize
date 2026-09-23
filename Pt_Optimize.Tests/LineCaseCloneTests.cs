using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★ R48 J 路（2026-09-15 Opus 5，合并把关待办 P1-1）：<see cref="FlangeAutoSizer.CloneCase"/> 的注释多年来写着「`LineCaseCloneTests` 盯着这件事」，
/// 而全仓并没有这个测试 —— 注释是空头支票。实测（deliverable/J路_J2_图纸路径截面J限值_本次开跑于2026-09-15_190514.txt）：
/// CloneCase 漏拷 14 个字段，其中 JDesignAPerMm2 漏拷使图纸路径算例 J 设 8 时「法兰截面 J」9.993 在直接解上限 9 不过、在副本上限 11 过。
/// 本类就是那个测试：**反射枚举 LineCase 的全部公开字段与可写属性**，给每个造一个非缺省值，克隆后逐个比；
/// 故意不拷的只有名单上的三个（并核它们确实没拷）；遇到门认不得的类型当场红，不许默默跳过。
/// </summary>
public class LineCaseCloneTests
{
    /// <summary>故意不拷的字段与理由（与 CloneCase 里的注释同一份口径）。</summary>
    private static readonly Dictionary<string, string> NotCopied = new()
    {
        ["BaselineRootC"] = "无法兰基线缓存：定尺寸器在跨轮处显式传",
        ["WarmStart"] = "外层耦合热启动状态：只在收敛时由定尺寸器显式传",
        ["CoupleTrace"] = "探针的逐轮回调：副本是试探解，不许把别的解的轮次混进探针记录",
        ["JacobianAmpCache"] = "R48 M（2026-09-18，Fable 5.1）：实测雅可比放大的缓存，与 BaselineRootC 同一条规矩（几何不同就要重量）",
    };

    private static object? Sentinel(Type t, object? current)
    {
        if (t == typeof(double)) return 7.25;
        if (t == typeof(int)) return 7;
        if (t == typeof(bool)) return !(bool)current!;
        if (t == typeof(string)) return "门值";
        if (t == typeof(double[])) return new[] { 7.25, 8.5 };
        if (t == typeof(double[][])) return new[] { new[] { 7.25 }, new[] { 8.5 } };
        if (t == typeof(string[])) return new[] { "门值.3dm" };
        if (t == typeof(FlangePlate[])) return new[] { new FlangePlate { DiscRadiusMm = 33.3 } };
        if (t == typeof(ThicknessField[])) return new[] { new ThicknessField { Step = 0.77 } };
        if (t == typeof(DesignInputs)) return new DesignInputs();
        if (t.IsEnum) return Enum.GetValues(t).Cast<object>().Last(v => !Equals(v, current));
        if (typeof(Delegate).IsAssignableFrom(t))
            return Delegate.CreateDelegate(t, typeof(LineCaseCloneTests).GetMethod(nameof(Trace), BindingFlags.NonPublic | BindingFlags.Static)!);
        return null;
    }

    private static void Trace(int a, LineResult b, double c, double d, double e, double f, double g, string h) { }

    private static bool Same(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a is string || !(a is IEnumerable)) return Equals(a, b);
        var ea = ((IEnumerable)a).Cast<object?>().ToList();
        var eb = ((IEnumerable)b).Cast<object?>().ToList();
        return ea.Count == eb.Count && ea.Zip(eb).All(x => Same(x.First, x.Second));
    }

    [Fact]
    public void 克隆带上LineCase的全部公开字段_故意不拷的只有名单上三个()
    {
        var src = new LineCase();
        var fields = typeof(LineCase).GetFields(BindingFlags.Public | BindingFlags.Instance).ToList();
        var unknown = new List<string>();
        foreach (var f in fields)
        {
            var v = Sentinel(f.FieldType, f.GetValue(src));
            if (v is null) { unknown.Add($"{f.Name}（{f.FieldType.Name}）"); continue; }
            f.SetValue(src, v);
        }
        Assert.True(unknown.Count == 0, "LineCase 有门认不得的字段类型 —— 补上 Sentinel，别跳过：" + string.Join("、", unknown));

        var clone = FlangeAutoSizer.CloneCase(src);
        var fresh = new LineCase();
        var lost = new List<string>();
        foreach (var f in fields)
        {
            if (NotCopied.ContainsKey(f.Name))
            {
                Assert.True(Same(f.GetValue(clone), f.GetValue(fresh)),
                    $"{f.Name} 在「故意不拷」名单上（{NotCopied[f.Name]}），副本却带上了 —— 要么改名单与 CloneCase 注释，要么别拷");
                continue;
            }
            if (!Same(f.GetValue(src), f.GetValue(clone))) lost.Add(f.Name);
        }
        Assert.True(lost.Count == 0, "CloneCase 漏拷：" + string.Join("、", lost));
        Assert.All(NotCopied.Keys, k => Assert.NotNull(typeof(LineCase).GetField(k)));   // 名单上的名字真的存在（改名后名单不许悬空）

        // 可写属性（目前只有 TabInsul3dmMm：逐片数组为空时读的是它自己的后备值）
        foreach (var pi in typeof(LineCase).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.CanRead))
        {
            var s2 = new LineCase();
            var v = Sentinel(pi.PropertyType, pi.GetValue(s2));
            Assert.True(v is not null, $"属性 {pi.Name}（{pi.PropertyType.Name}）门认不得");
            pi.SetValue(s2, v);
            Assert.True(Same(pi.GetValue(s2), pi.GetValue(FlangeAutoSizer.CloneCase(s2))), $"CloneCase 漏拷属性 {pi.Name}");
        }
    }

    /// <summary>
    /// 界面：图纸模式造的算例带着 ① 页设定的 J（此前只有解析路径赋值，图纸路径恒为缺省 10 ⇒ 截面 J 限值恒 11）。
    /// 页面 BuildCase 是私有的，这里用反射调它本身（不手抄造算例的配方）。
    /// </summary>
    [Fact]
    public void 图纸模式造的算例带着设定J()
    {
        var page = new LineDesignPage(new DesignInputs());
        T F<T>(string name) => (T)typeof(LineDesignPage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        F<System.Windows.Forms.RadioButton>("_src3dm").Checked = true;
        F<System.Windows.Forms.NumericUpDown>("_jDesign").Value = 8m;
        foreach (var tb in F<System.Windows.Forms.TextBox[]>("_file3dm")) tb.Text = "门用.3dm";
        var build = typeof(LineDesignPage).GetMethod("BuildCase", BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!;
        var lc = (LineCase)build.Invoke(page, null)!;
        Assert.NotEmpty(lc.FlangeFile3dm);
        Assert.Empty(lc.FlangePlates);
        Assert.Equal(8.0, lc.JDesignAPerMm2);
        Assert.Equal(8.0, FlangeAutoSizer.CloneCase(lc).JDesignAPerMm2);
        Assert.Equal(9.0, SectionSizing.JCheckOf(FlangeAutoSizer.CloneCase(lc).JDesignAPerMm2));
    }
}
