using System.Collections.Generic;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **判据缺席**——铁律三「判据只能过 / 不过 / 无法判定，绝不允许消失」的执行机制。
///
/// 为什么需要一整个测试档：这个坑本项目踩过三次，每次都是**就地补一个 else**：
///   · ③ 2026-08-15（基线算不出来时整条消失 ⇒ 报「✓ 全过」而增量降 max 是 +32，上限 10）
///   · ⑤ 2026-08-17（.3dm 模式下 FlangePlates 为空，同一形态换个判据）
///   · ②″ 2026-08-24（所有片的 TDiscMaxC 都是 NaN ⇒ 整条消失；**漏了一年多**）
/// 三次都靠人记得写 else，于是第四次一定还会发生。
/// <see cref="LineResult.Required"/> 把「该有哪几条」变成一份**数据**，
/// 本档把那份数据变成**会自己跑的门**。
///
/// ⚠ 危险的不是「判据不过」——那会被看见。是「判据消失」：
///   AllOk 的全称量词只在**剩下的**判据上取，缺的那条不投反对票。
/// </summary>
public class RequiredChecksTests
{
    private static ConstraintOut Pass(string name, CheckKind kind) =>
        new() { Name = name, Kind = kind, Ok = true, Actual = 1.0, Limit = 2.0 };

    /// <summary>造一张「该有的都有、而且条条都过」的表。</summary>
    private static LineResult Table(bool ramp = true, string? drop = null)
    {
        var r = new LineResult { Converged = true, Ok = true, RampChecked = ramp };
        r.Checks = LineResult.Required
            .Where(q => (!q.NeedsRamp || ramp) && q.Prefix != drop)
            // 名字带后缀：判据在实作里都是「前缀 + 说明」（如「③ 法兰增量温降 ≤ 上限」），
            // 用 StartsWith 找。若这里写成精确名，测试就绕过了真实的匹配方式。
            .Select(q => Pass(q.Prefix + " 某某后缀", q.Kind))
            .Append(Pass("· 某参考量", CheckKind.Reference))
            .ToArray();
        return r;
    }

    public static IEnumerable<object[]> EveryRequiredKey() =>
        LineResult.Required.Select(q => new object[] { q.Prefix });

    /// <summary>自证：名单非空、且只含参与判定的两种 Kind。否则下面每一条都空转。</summary>
    [Fact]
    public void Required_IsNonEmpty_AndOnlyJudgingKinds()
    {
        Assert.NotEmpty(LineResult.Required);
        Assert.All(LineResult.Required, q =>
            Assert.True(q.Kind is CheckKind.HardSafety or CheckKind.Target,
                $"{q.Prefix} 的 Kind 是 {q.Kind} —— 参考量不参与 AllOk，" +
                "把它列进必备名单会把「参考量算不出来」误判成「设计不合格」"));
        // 名单里不该有重复前缀（重复会让「缺一条」在 Failed 里报两遍）
        Assert.Equal(LineResult.Required.Length,
                     LineResult.Required.Select(q => q.Prefix).Distinct().Count());
    }

    /// <summary>自证：齐全的表确实全过。不先钉住这条，下面「拿掉一条就不过」全是空转。</summary>
    [Fact]
    public void FullTable_Passes()
    {
        var r = Table();
        Assert.Empty(r.MissingChecks);
        Assert.True(r.AllOk, "齐全且条条通过的表却判不过：" + string.Join("；", r.Failed));
        Assert.True(r.HardOk);
        Assert.Empty(r.Failed);
    }

    /// <summary>核心：**任意**一条必备判据整条消失 ⇒ 不许再报「全过」。</summary>
    [Theory]
    [MemberData(nameof(EveryRequiredKey))]
    public void AnyMissingRequiredCheck_KillsAllOk(string key)
    {
        var r = Table(ramp: true, drop: key);

        Assert.Contains(key, r.MissingChecks);
        Assert.False(r.AllOk, $"拿掉「{key}」之后仍报全过 —— 判据消失被当成了通过");
        // 光让 AllOk 变 false 不够：界面要打「✗」后面跟原因，
        // 否则就是「不过，但说不出哪儿不过」，比不报还难查。
        // ★ 2026-08-30：Failed 印出来的名字**剥掉了判据代号**（界面严禁代号），
        //   所以这里按剥壳后的名字比 —— key 本身（内部身份）没变，仍在 MissingChecks 里。
        string plain = Criteria.Plain(key);
        Assert.Contains(r.Failed, f => f.Contains(plain) && f.Contains("缺席"));
        Assert.DoesNotContain(r.Failed, f => f.Contains("②′") || f.Contains("②″"));
    }

    /// <summary>硬安全线缺一条 ⇒ HardOk 也必须为 false（形状体检那一关读的是它）。</summary>
    [Theory]
    [MemberData(nameof(EveryRequiredKey))]
    public void MissingHardCheck_KillsHardOk(string key)
    {
        var q = LineResult.Required.First(x => x.Prefix == key);
        var r = Table(ramp: true, drop: key);
        if (q.Kind == CheckKind.HardSafety)
            Assert.False(r.HardOk, $"硬安全线「{key}」整条消失，HardOk 仍为真");
        else
            // 自证：Target 缺席不该影响 HardOk —— 否则上面那条恒成立，什么也没验到
            Assert.True(r.HardOk, $"「{key}」是 {q.Kind}，不该拖垮 HardOk");
    }

    /// <summary>
    /// ① 在 CheckRamp=false 时是**合法缺席**（定尺寸内循环故意关掉它省时间），
    /// 但 CheckRamp=true 时缺席就是错。两个方向都验，否则「合法缺席」会变成万能借口。
    /// </summary>
    [Fact]
    public void RampCheck_IsRequiredOnlyWhenItWasRun()
    {
        // ★ R20（2026-09-08）：① 改闭式（升温所需电流折成管 J ≤ 许用），**每轮都在**，不再随 CheckRamp 缺席。
        //   集总升温用时那条降为参考量（Key.RampHours），参考量按定义不在必备名单。
        var off = Table(ramp: false);
        Assert.Empty(off.MissingChecks);
        Assert.True(off.AllOk, "① 闭式每轮都在；表里有它就不该缺席");

        var offDrop = Table(ramp: false, drop: LineResult.Key.Ramp);
        Assert.Contains(LineResult.Key.Ramp, offDrop.MissingChecks);
        Assert.False(offDrop.AllOk, "R20 之后 ① 不再有「合法缺席」");

        var on = Table(ramp: true, drop: LineResult.Key.Ramp);
        Assert.Contains(LineResult.Key.Ramp, on.MissingChecks);
        Assert.False(on.AllOk, "说好了要评 ① 却没评，仍报全过");
    }

    /// <summary>
    /// 空表不许报「硬安全线全过」。
    /// 旧写法是 `Checks.Where(硬).All(过)` —— **空集上 All 恒真**，
    /// 于是一张什么都没有的表会得到 HardOk = true。这是「空集通过的断言」家族。
    /// </summary>
    [Fact]
    public void EmptyTable_IsNotSilentlyOk()
    {
        var r = new LineResult { Converged = true, Ok = true, RampChecked = true };
        Assert.False(r.HardOk, "空判据表报出「硬安全线全过」—— 空集恒真的老毛病");
        Assert.False(r.AllOk);
        Assert.Equal(LineResult.Required.Length, r.MissingChecks.Length);
    }

    /// <summary>「无法判定」与「缺席」是两回事，但都不算通过 —— 两者都要能各自单独拦住。</summary>
    [Fact]
    public void Undetermined_AndMissing_AreBothNotPass()
    {
        var und = Table();
        var i = System.Array.FindIndex(und.Checks,
            c => c.Name.StartsWith(LineResult.Key.DiscTemp, System.StringComparison.Ordinal));
        Assert.True(i >= 0, "自证：表里本来就该有 ②″，否则下面验的是空气");
        und.Checks[i].Ok = false;
        und.Checks[i].Undetermined = true;
        und.Checks[i].Actual = double.NaN;

        Assert.Empty(und.MissingChecks);          // 它在场，只是判不了
        Assert.False(und.AllOk);
        Assert.Contains(und.Failed, f => f.Contains("无法判定"));
        Assert.DoesNotContain(und.Failed, f => f.Contains("缺席"));
    }
}
