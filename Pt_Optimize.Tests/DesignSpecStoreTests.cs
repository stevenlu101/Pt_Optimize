using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 设计记录的**文件形态**：存出去、读回来，必须是同一个设计。
///
/// ★ 为什么要有这一组（2026-08-23）：
///   第一版的 DTO **漏了七个几何字段**（tabFilletMm / ringWidthMm / clampLengthMm /
///   flangeInsul* / setpointC / clampTempC）。档看起来完整、也读得进来，
///   但 BuildCase 拿默认值补上缺口 ⇒ 造出的是**另一个零件**：
///   试档实测 ③ = 132.9 K，而记录值是 5.182 K。
///
///   --selfcheck A 段当场抓住了它 —— 但那是**运气**：只有当记录值来自另一次
///   （字段完整的）运行时才对不上。如果档是程序一次写全的，几何缺字段与记录值
///   会**自洽**，A 段照样通过，而档里存的仍不是你看到的那个设计。
///
///   ⇒ 真正的守卫是这里：**存→读往返，重建出的算例必须逐字段相同**。
///     它不依赖任何人记得「加字段时要同步 DTO」。
/// </summary>
public class DesignSpecStoreTests
{
    /// <summary>把 Save/LoadAll 换到临时目录跑，不碰仓库里的 finaldesigns/。</summary>
    private static string NewTempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "fdstore_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>直接调私有 Parse，绕开目录查找 —— 本组要验的是「字段全不全」。</summary>
    private static DesignSpec Parse(string json)
    {
        var m = typeof(DesignSpecStore).GetMethod("Parse",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        try { return (DesignSpec)m.Invoke(null, new object[] { json, "test.fd.json" })!; }
        // ⚠ 反射调用会把异常裹进 TargetInvocationException ⇒ 断言看到的是**外壳**的消息，
        //   而不是「缺 tabThickMm」这种真正有信息的那句。拆掉壳再抛。
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { throw ex.InnerException; }
    }

    private static string Serialize(DesignSpec fd)
    {
        string dir = NewTempDir();
        try
        {
            // Save 会按 fd.Name 起文件名，读回原文即可
            string path = (string)typeof(DesignSpecStore)
                .GetMethod("Save", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { fd })!;
            return File.ReadAllText(path);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// ★★ 往返必须无损：内置四档各存一次、读回来，**BuildCase 造出的算例逐字段相同**。
    ///
    /// 比的是 LineCase 而不是 DesignSpec 的字段 —— 因为真正决定结果的是它。
    /// 漏字段、把导出量当本源存、单位搞错，都会在这里现形。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RoundTrip_RebuildsIdenticalCase(int idx)
    {
        var src = DesignSpec.Builtin[idx];
        var p = new DesignInputs();

        // 存→读。Save 落在仓库的 finaldesigns/ 下会与真档打架 ⇒ 换个名字，用完删掉。
        var tmp = src.Clone();
        tmp.Name = "★往返测试★ " + src.Name + " " + Guid.NewGuid().ToString("N")[..6];
        string? written = null;
        try
        {
            written = DesignSpecStore.Save(tmp);
            var back = Parse(File.ReadAllText(written));

            var a = src.BuildCase(p, checkRamp: true);
            var b = back.BuildCase(p, checkRamp: true);

            Assert.Equal(a.WallMm, b.WallMm, 12);
            Assert.Equal(a.TubeIdMm, b.TubeIdMm, 12);
            Assert.Equal(a.SegLengthMm, b.SegLengthMm, 12);
            Assert.Equal(a.ClampTempC.Length, b.ClampTempC.Length);
            Assert.Equal(a.SetpointC, b.SetpointC);
            Assert.Equal(a.FlangePlates.Length, b.FlangePlates.Length);

            for (int j = 0; j < a.FlangePlates.Length; j++)
            {
                var (x, y) = (a.FlangePlates[j], b.FlangePlates[j]);
                Assert.Equal(x.DiscRadiusMm, y.DiscRadiusMm, 12);
                Assert.Equal(x.HoleRadiusMm, y.HoleRadiusMm, 12);
                Assert.Equal(x.TabEndXMm, y.TabEndXMm, 12);
                Assert.Equal(x.TabEndHalfWidthMm, y.TabEndHalfWidthMm, 12);
                Assert.Equal(x.ThicknessMm, y.ThicknessMm, 12);
                Assert.Equal(x.TabInsulThickMm, y.TabInsulThickMm, 12);
                Assert.Equal(x.TabFilletMm, y.TabFilletMm, 12);
                Assert.Equal(x.WeldFilletLegMm, y.WeldFilletLegMm, 12);
                Assert.Equal(x.TabParallel, y.TabParallel);
                Assert.Equal(x.DiscStepRadiiMm, y.DiscStepRadiiMm);
                Assert.Equal(x.DiscStepThicknessMm, y.DiscStepThicknessMm);
            }
        }
        finally { if (written is not null) { try { File.Delete(written); } catch { } } }
    }

    /// <summary>
    /// 造一个**每个可存字段都偏离默认值**的档。
    ///
    /// ★★ 这是本组的关键（2026-08-23 靠注入测试才发现）：
    ///   只拿内置四档做往返，是**测不出漏字段的**——
    ///   内置档的 TabFilletMm 等好几项本来就等于 DesignSpec 的默认值，
    ///   于是「读档时漏掉这一项」两边照样相等，断言照样打勾。
    ///   （实测：把 `fd.TabFilletMm = tf` 那行注掉，29 项全过。）
    ///   ⇒ 每个字段都必须与默认值**不同**，漏任何一个才会现形。
    /// </summary>
    private static DesignSpec Distinctive()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.WallMm = 0.77;
        d.TubeInsulMm = 13.5;
        d.DiscRadiusMm = 33.0;
        d.TabLengthMm = 151.0;
        d.TabHalfWidthMm = 27.0;
        d.TabFilletMm = 4.5;                 // ← 注入测试当初漏的就是它
        d.RingWidthMm = 4.25;
        d.FlangeInsulMm = 17.5;
        d.FlangeInsulated = false;           // 默认是 true
        d.ClampLengthMm = 37.0;
        d.ClampTempC = 285.0;
        d.SetpointC = new[] { 1141.0, 1071.0, 1041.0 };
        d.TabThickMm = new[] { 0.91, 2.41, 2.31, 0.71 };
        d.TabInsulMm = new[] { 0.35, 0.45, 0.55, 0.25 };
        d.RingMul = new[] { 1.05, 1.15, 1.25, 1.35 };
        d.ResidualK = 0.75;
        return d;
    }

    /// <summary>
    /// ★★ 每个字段都偏离默认值的档，往返之后**造出的算例必须逐字段相同**。
    /// 漏读任何一项，这里立刻红 —— 上面那条拿内置档跑的验不出来。
    /// </summary>
    [Fact]
    public void RoundTrip_LosesNoField()
    {
        var src = Distinctive();
        src.Name = "★往返测试★ 全字段 " + Guid.NewGuid().ToString("N")[..6];
        var p = new DesignInputs();
        string? w = null;
        try
        {
            w = DesignSpecStore.Save(src);
            var back = Parse(File.ReadAllText(w));

            // ★ 比**整份 LineCase 的序列化**，不是我手列的那几项。
            //   手列清单会漏 —— 实测漏过一次：`clampLengthMm` 进的是 LineCase.Base
            //   （DesignInputs.BusbarClampLengthMm），而我只比了管/段/法兰片，
            //   于是漏读它照样通过。覆盖面不该由「我记得列哪些」决定。
            string J(LineCase c) => System.Text.Json.JsonSerializer.Serialize(c,
                new System.Text.Json.JsonSerializerOptions
                {
                    IncludeFields = true,
                    // LineCase 里有 NaN 哨兵（如 TabThicknessMm）——
                    // 不开这个，序列化本身就抛，比较无从谈起。
                    NumberHandling = System.Text.Json.Serialization
                                     .JsonNumberHandling.AllowNamedFloatingPointLiterals,
                });
            Assert.Equal(J(src.BuildCase(p, checkRamp: true)),
                         J(back.BuildCase(p, checkRamp: true)));
            // 顺带钉住「这个档确实偏离了默认」——否则本例又会退化成恒真
            var def = new DesignSpec();
            Assert.NotEqual(def.TabFilletMm, src.TabFilletMm);
            Assert.NotEqual(def.RingWidthMm, src.RingWidthMm);
            Assert.NotEqual(def.ClampLengthMm, src.ClampLengthMm);
            Assert.NotEqual(def.FlangeInsulated, src.FlangeInsulated);
        }
        finally { if (w is not null) { try { File.Delete(w); } catch { } } }
    }

    /// <summary>五个回归基准值也要原样带回来 —— 少一个，A 段就无从对账。</summary>
    [Fact]
    public void RoundTrip_KeepsRecordedChecks()
    {
        var src = DesignSpec.Builtin[0];
        var tmp = src.Clone();
        tmp.Name = "★往返测试★ checks " + Guid.NewGuid().ToString("N")[..6];
        string? w = null;
        try
        {
            w = DesignSpecStore.Save(tmp);
            var back = Parse(File.ReadAllText(w));
            Assert.Equal(src.RampH, back.RampH, 12);
            Assert.Equal(src.DiscOverK, back.DiscOverK, 12);
            Assert.Equal(src.HoleFluxW, back.HoleFluxW, 12);
            Assert.Equal(src.FlangeDipK, back.FlangeDipK, 12);
            Assert.Equal(src.TubeJ, back.TubeJ, 12);
            Assert.Equal(src.TotalMassG, back.TotalMassG, 9);
        }
        finally { if (w is not null) { try { File.Delete(w); } catch { } } }
    }

    /// <summary>
    /// 缺必填项要**拒绝**，不许拿默认值凑。
    /// 默认值凑出来的是一个「看起来正常」的错设计 —— 本项目最忌的形状。
    /// </summary>
    [Theory]
    [InlineData("name")]
    [InlineData("wallMm")]
    [InlineData("discRadiusMm")]
    [InlineData("tabThickMm")]
    [InlineData("checks")]
    public void Parse_RejectsMissingRequired(string drop)
    {
        var src = DesignSpec.Builtin[0].Clone();
        src.Name = "★往返测试★ drop-" + drop;
        string? w = null;
        try
        {
            w = DesignSpecStore.Save(src);
            string json = File.ReadAllText(w);
            // 把那一项改名 ⇒ 等价于「缺了它」
            string broken = json.Replace("\"" + drop + "\"", "\"__" + drop + "__\"");
            Assert.NotEqual(json, broken);                 // 确保真的改到了（否则本例空转）
            Assert.ThrowsAny<Exception>(() => Parse(broken));
        }
        finally { if (w is not null) { try { File.Delete(w); } catch { } } }
    }

    /// <summary>数组长度不对也要拒绝 —— 三片厚度的档会静默造出一台三片法兰的机器。</summary>
    [Fact]
    public void Parse_RejectsWrongArrayLength()
    {
        var src = DesignSpec.Builtin[0].Clone();
        src.Name = "★往返测试★ len " + Guid.NewGuid().ToString("N")[..6];
        string? w = null;
        try
        {
            w = DesignSpecStore.Save(src);
            string json = File.ReadAllText(w);
            var ex = Record.Exception(() => Parse(
                System.Text.RegularExpressions.Regex.Replace(
                    json, "\"tabThickMm\":\\s*\\[[^\\]]*\\]", "\"tabThickMm\": [1.0, 2.0, 3.0]")));
            Assert.NotNull(ex);
            Assert.Contains("tabThickMm", ex!.Message, StringComparison.Ordinal);
        }
        finally { if (w is not null) { try { File.Delete(w); } catch { } } }
    }

    /// <summary>同名不许静默覆盖 —— 悄悄换掉一份回归基准，比没有基准更坏。</summary>
    [Fact]
    public void Save_RefusesOverwrite()
    {
        var src = DesignSpec.Builtin[0].Clone();
        src.Name = "★往返测试★ dup " + Guid.NewGuid().ToString("N")[..6];
        string? w = null;
        try
        {
            w = DesignSpecStore.Save(src);
            Assert.Throws<IOException>(() => DesignSpecStore.Save(src));
        }
        finally { if (w is not null) { try { File.Delete(w); } catch { } } }
    }
}
