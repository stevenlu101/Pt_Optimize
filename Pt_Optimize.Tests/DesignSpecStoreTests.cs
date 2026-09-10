using System;
using System.Collections.Generic;
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
    internal static DesignSpec Parse(string json)
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
            // ⚠ 2026-09-03 起 SegLengthMm 是**逐段数组**（用户：每段长度可单独设定）⇒
            //   不能再按标量比。整段比 —— 少一段、错一段都要红。
            Assert.Equal(a.SegLengthMm, b.SegLengthMm);
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
        // ★ 2026-09-03 新增：每段长度各不相同 —— 三个都一样的话，
        //   「按下标错位」这类漏法在往返里看不出来（本样本存在的意义就是不许有这种盲区）。
        d.SegLengthMm = new[] { 231.0, 317.0, 428.0 };
        d.TubeInsulMm = 13.5;
        d.TubeIdMm = 60.0;                                     // R30（2026-09-10）：管内径进几何
        d.JDesignAPerMm2 = 12.5;             // 2026-09-09：设计电流密度 J 进设计（预设 10）——不偏离预设本门就空转
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
        d.TongueThickMm = new[] { 0.61, 1.21, 1.11, 0.51 };
        d.TabArmX0Mm = new[] { -22.0, -20.0, -18.0, -16.0 };      // R29（2026-09-10）：舌根加厚带（派生量也要往返）
        d.TabArmX1Mm = new[] { -2.0, 0.0, -1.0, -0.5 };
        d.TabArmThickMm = new[] { 2.51, 3.11, 2.71, 2.31 };   // R11（2026-09-08）：舌片厚逐片；默认 NaN 哨兵 ⇒ 要给非默认值本门才不空转
        // ★ 2026-09-02 补：这三个 08-30 就成了设计的一部分，而本样本停在 08-23 ⇒
        //   它们一直等于默认值（NaN 哨兵），于是「档里没存 t₂」漏读照样相等，本门空转。
        //   t₂ 尤其要命：它是求解器治「管孔净流入」的首选旋钮。
        d.RingW1Mm = new[] { 1.5, 2.5, 3.5, 4.5 };
        d.RingW2Mm = new[] { 5.5, 6.5, 7.5, 8.5 };
        d.RingMul2 = new[] { 1.11, 1.22, 1.33, 1.44 };
        // ★ 圆盘背侧减重槽（2026-09-05）。四片给**四个不同的数** ——
        //   给一样的数时「按下标错位」这类漏法在往返里看不出来。
        d.SlotSpanDeg = new[] { 30.0, 60.0, 90.0, 120.0 };
        d.SlotRInMm = 28.5; d.SlotROutMm = 41.5;
        // ★ 舌板开孔孔径（用户要求 R5）。四片四个不同的数 —— 理由同上。
        d.TabHoleRMm = new[] { 2.5, 3.5, 4.5, 5.5 };
        d.TabHoleAspect = new[] { 1.2, 1.5, 1.8, 2.1 };
        // ★ R12/R13（2026-09-09）：孔心改逐片、槽心角、两个形状族、长椭圆轴向 —— 四片四个不同的数，理由同上。
        //   形状族只有 0/3/4 与 0/1 可选：给两片不同的非默认值，另两片留默认（错位也看得出）。
        d.TabHoleXMm = new[] { -77.0, -81.0, -85.0, -89.0 };
        d.SlotCenterDeg = new[] { 10.0, 95.0, -80.0, 170.0 };
        d.TabHoleSides = new[] { 3.0, 4.0, 0.0, 3.0 };
        d.DiscCutShape = new[] { 1.0, 0.0, 1.0, 0.0 };
        d.DiscCutRotDeg = new[] { 100.0, double.NaN, 15.0, -60.0 };
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

    /// <summary>
    /// ★★★★★ **样本自己不许过期**（2026-09-02）。
    ///
    /// 上面那条门（RoundTrip_LosesNoField）设计得对，而它 2026-08-30 起**一直在空转**：
    /// t₂／r₁／r₂ 那天成了设计的一部分，<see cref="Distinctive"/> 却停在 08-23 ——
    /// 新字段本来就等于默认值，于是「档里没存 t₂」漏读照样相等，门照样打勾。
    /// 实测后果：求解器解出含 t₂ 的设计 → 存档 → 载入后是**另一个设计**。
    ///
    /// ⇒ 本门盯的是**样本**：DesignSpec 里每一个会进设计的字段，样本都必须偏离默认值。
    ///   下一个新旋钮加进来而忘了更新样本，这里当场红 —— 不靠任何人记得。
    ///
    /// ⚠ 豁免名单只放**确实不进设计**的（身份/出处/记录值/口径）。往里加东西要写清为什么。
    /// </summary>
    [Fact]
    public void 样本必须覆盖每一个会进设计的字段()
    {
        // 不进 BuildCase 的：身份与出处、五个回归记录值、加密复算的口径、以及只读导出量。
        var skip = new HashSet<string>
        {
            "Name", "Provenance", "Binding", "Invalid", "InvalidChecks", "FromFile",
            "TotalMassG", "TubeMassG", "FlangeMassG", "ResidualK",
            "RampH", "DiscOverK", "HoleFluxW", "FlangeDipK", "TubeJ",
            "VerifiedMeshMm", "VerifiedFlangeDipK", "VerifiedHoleFluxW",
            "VerifiedDiscOverK", "VerifiedNote",
        };
        var def = new DesignSpec();
        var got = Distinctive();
        var same = new List<string>();
        foreach (var f in typeof(DesignSpec).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (skip.Contains(f.Name)) continue;
            object? a = f.GetValue(def), b = f.GetValue(got);
            bool equal = a is double[] da && b is double[] db
                ? da.Length == db.Length && da.Zip(db).All(t =>
                      (double.IsNaN(t.First) && double.IsNaN(t.Second)) || t.First.Equals(t.Second))
                : Equals(a, b);
            if (equal) same.Add(f.Name);
        }
        Assert.True(same.Count == 0,
            "Distinctive() 里这些字段还等于默认值 ⇒ 往返测试对它们是**空转**（漏存也验不出来）："
            + string.Join("、", same)
            + "。要么给它一个偏离默认的值，要么写清为什么它不进设计再加进豁免名单。");

        // 自证：豁免名单里的名字都得真的存在，否则打错字就等于偷偷放行
        var all = typeof(DesignSpec).GetFields(BindingFlags.Public | BindingFlags.Instance)
                                    .Select(f => f.Name).ToHashSet();
        var ghost = skip.Where(n => !all.Contains(n)).ToArray();
        Assert.True(ghost.Length == 0, "豁免名单里有不存在的字段：" + string.Join("、", ghost));
    }

    /// <summary>
    /// ★★★★★ A2（2026-09-02 用户拍板）：**判据值的口径要往返无损**。
    ///
    /// checks 里那五个数一直是存的，但**没说它们是在哪张网格上算的**。同一个设计：
    /// <code>
    ///   粗网格（导航 2 mm）   法兰增量温降  4.720 K   看着余量 53 %
    ///   加密复算到数不再变     法兰增量温降 10.329 K   越限
    /// </code>
    /// 不存口径，档自己说不清拿的是哪一个 —— 现役两档的记录值正是这么来的。
    /// </summary>
    [Fact]
    public void RoundTrip_保住加密复算的口径()
    {
        var src = Distinctive();
        src.Name = "★往返测试★ 口径 " + Guid.NewGuid().ToString("N")[..6];
        src.VerifiedMeshMm = 0.250;
        src.VerifiedFlangeDipK = 10.329;
        src.VerifiedHoleFluxW = 3.386;
        src.VerifiedDiscOverK = -0.004;
        src.VerifiedNote = "⚠ 越限，工程师判断后仍保存";
        string? w = null;
        try
        {
            w = DesignSpecStore.Save(src);
            var back = Parse(File.ReadAllText(w));
            Assert.Equal(src.VerifiedMeshMm,     back.VerifiedMeshMm);
            Assert.Equal(src.VerifiedFlangeDipK, back.VerifiedFlangeDipK);
            Assert.Equal(src.VerifiedHoleFluxW,  back.VerifiedHoleFluxW);
            Assert.Equal(src.VerifiedDiscOverK,  back.VerifiedDiscOverK);
            Assert.Equal(src.VerifiedNote,       back.VerifiedNote);
        }
        finally { if (w is not null) { try { File.Delete(w); } catch { } } }
    }

    /// <summary>
    /// ★ 没做过加密复算的档：整组必须仍是 NaN（「没验过」），**不许变成 0**。
    /// 存 0 会让「没验过」看起来像「验过且是 0」—— 与把失败画成通过同一族。
    /// </summary>
    [Fact]
    public void RoundTrip_没验过就该还是没验过()
    {
        var src = Distinctive();
        src.Name = "★往返测试★ 没验过 " + Guid.NewGuid().ToString("N")[..6];
        // ⚠ Distinctive() 克隆的是 W08，而它**有**复核值 ⇒ 这里必须显式清掉。
        //   （第一版没清，断言当场红 —— 比默默通过好。）
        src.VerifiedMeshMm = src.VerifiedFlangeDipK =
            src.VerifiedHoleFluxW = src.VerifiedDiscOverK = double.NaN;
        src.VerifiedNote = "";
        Assert.True(double.IsNaN(src.VerifiedMeshMm), "清过之后才谈得上「没验过」");
        string? w = null;
        try
        {
            w = DesignSpecStore.Save(src);
            Assert.DoesNotContain("\"verified\"", File.ReadAllText(w));   // 整项不写，不是写一堆 null
            var back = Parse(File.ReadAllText(w));
            Assert.True(double.IsNaN(back.VerifiedMeshMm));
            Assert.True(double.IsNaN(back.VerifiedFlangeDipK));
            Assert.Equal("", back.VerifiedNote);
        }
        finally { if (w is not null) { try { File.Delete(w); } catch { } } }
    }

    /// <summary>
    /// ★★ A3：**t₂／r₁／r₂ 逐片往返**，含「只有某几片有值」的部分设定。
    /// 它们用 NaN 当哨兵，而 JSON 写不了 NaN ⇒ 逐片映射 NaN ↔ null。
    /// 整组映射（有一个 NaN 就整项丢掉）会把部分设定悄悄抹平。
    /// </summary>
    [Fact]
    public void RoundTrip_渐变环旋钮的部分设定也要保住()
    {
        var src = Distinctive();
        src.Name = "★往返测试★ 部分 t2 " + Guid.NewGuid().ToString("N")[..6];
        src.RingMul2 = new[] { double.NaN, 1.37, double.NaN, 1.09 };   // 只有两片有
        string? w = null;
        try
        {
            w = DesignSpecStore.Save(src);
            var back = Parse(File.ReadAllText(w));
            Assert.True(double.IsNaN(back.RingMul2[0]));
            Assert.Equal(1.37, back.RingMul2[1]);
            Assert.True(double.IsNaN(back.RingMul2[2]));
            Assert.Equal(1.09, back.RingMul2[3]);
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
