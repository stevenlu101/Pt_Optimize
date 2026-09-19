using System;
using System.Linq;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R26（2026-09-09）：**加密复算发现「导航网格上过、细网格上不过」之后不许死循环**。
///
/// ══ 病灶（HANDOVER R26 行，Ø56 盘那份实例）
///
/// 「核算整线」流水线在「◆ 加密复算」（<c>VerifyMeshAsync</c> → <c>MeshVerify.Run</c>）
/// 发现细网格上判据不过（导航网格全过，0.125 mm 上 ③ 法兰增量温降 11.76 > 10）时，
/// <c>_last</c> 会被换成那份细网格结果（只要网格无关——<c>res.Converged</c>——就换，
/// 不问 <c>AllOk</c>，见 <c>VerifyMeshAsync</c> 的长注释）。<c>Flow.Next</c> 于是落进
/// 「判据没全过」那一支，原来一律指「自动定厚」；而自动定厚只在**导航网格**上求根
/// （D8 默认 FineMm = 0）⇒ 又全过 ⇒ 指路又要求加密复算 ⇒ 又不过 ⇒ 两个按钮**交替指、
/// 走满 8 步白跑**。
///
/// ══ 修法
///
/// 在「判据没全过」那一支的最前面加一条规则：复核过（<c>MeshVerified &amp;&amp;
/// VerifiedFresh</c>）之后仍不过 ⇒ 直接指「◆ 细网格重解」——在细网格上重新求根，
/// 不必回导航网格重来一遍。
///
/// 本文件只验 <see cref="Flow.Next"/> 这一处决定，不碰界面（界面接线见
/// <c>tests/UiWiring/Program.cs</c> §28 的对应小节）。
/// </summary>
public class FineResolveAfterVerifyTests
{
    /// <summary>
    /// 造一个「收敛、新鲜、除一条 HardSafety（法兰增量温降）外全过」的判据表 ——
    /// 这正是「细网格重解」要接手的那个状态：加密复算换过来的解，某条判据不过。
    ///
    /// ⚠ 底表必须**完整**（本仓 2026-08-24 的教训）：真解出来的表必定含
    ///   <see cref="LineResult.Required"/> 的全部条目，只造一条的夹具在真机上不可能出现。
    /// </summary>
    private static FlowState VerifiedButFailing(bool meshVerified)
    {
        var snap = new object();
        ConstraintOut C(string name, bool ok, CheckKind k) => new()
        { Name = name, Ok = ok, Kind = k, Actual = ok ? 1 : 9, Limit = 5 };

        var bad = C(LineResult.Key.FlangeDip, false, CheckKind.HardSafety);
        var last = new LineResult
        {
            Ok = true,
            Converged = true,
            RampChecked = true,
            Checks = LineResult.RequiredFor(emptyTube: false)   // K 路（2026-09-15 Opus 5）：必备名单按工况取；界面只解带玻璃稳态
                .Where(q => !bad.Name.StartsWith(q.Prefix, StringComparison.Ordinal))
                .Select(q => C(q.Prefix + " 底表", true, q.Kind))
                .Concat(new[] { bad }).ToArray(),
        };

        var st = new FlowState { Last = last, SolvedSnap = snap, CurrentSnap = snap };
        if (meshVerified)
        {
            // ★ 与 VerifyMeshAsync 的实况一致：复核那一刻的快照 == 当前快照（参数没再动过）。
            st.MeshVerified = true;
            st.VerifiedSnap = snap;
        }
        return st;
    }

    private static bool Applicable(string id) =>
        id is "core.runLine" or "core.autoThick" or "shape.search"
           or "core.verifyMesh" or "core.fineResolve";

    /// <summary>★★★ 主门：复核过、细网格上仍不过 ⇒ 指「◆ 细网格重解」，不是「自动定厚」。</summary>
    [Fact]
    public void 复核过后仍不过_指向细网格重解()
    {
        var st = VerifiedButFailing(meshVerified: true);
        Assert.True(st.MeshVerified && st.VerifiedFresh, "夹具没有真的走到「复核过」这个前提");

        var ns = Flow.Next(st, Applicable);
        Assert.NotNull(ns);
        Assert.Equal("core.fineResolve", ns!.CmdId);
        Assert.Contains("细网格", ns.Why);
    }

    /// <summary>
    /// ★ 自证：同样的判据表，**没有**复核过时仍然指「自动定厚」——
    /// 证明新规则真的是在读 MeshVerified，不是把「判据没全过」整支都改了。
    /// </summary>
    [Fact]
    public void 没有复核过时仍指自动定厚()
    {
        var st = VerifiedButFailing(meshVerified: false);
        Assert.False(st.MeshVerified);

        var ns = Flow.Next(st, Applicable);
        Assert.NotNull(ns);
        Assert.Equal("core.autoThick", ns!.CmdId);
    }

    /// <summary>★ 复核之后又动过参数 ⇒ 复核作废 ⇒ 不许指细网格重解（假绿灯比没复核更坏）。</summary>
    [Fact]
    public void 复核后又动参数_不指细网格重解()
    {
        var st = VerifiedButFailing(meshVerified: true);
        st.VerifiedSnap = new object();               // 与 CurrentSnap 不再相等
        Assert.False(st.VerifiedFresh);

        var ns = Flow.Next(st, Applicable);
        Assert.NotNull(ns);
        Assert.NotEqual("core.fineResolve", ns!.CmdId);
    }

    /// <summary>新命令登记在册、只属于一个阶段、且排在 core.verifyMesh 之后（Flow.SelfTest 兜底）。</summary>
    [Fact]
    public void 新命令登记在册且排在加密复算之后()
    {
        var c = Flow.Cmd("core.fineResolve");
        Assert.False(string.IsNullOrWhiteSpace(c.Text));
        Assert.Equal(StageId.整线核算, c.Stage);

        var ids = Flow.Stage(StageId.整线核算).CommandIds;
        int iVerify = Array.IndexOf(ids, "core.verifyMesh");
        int iFine = Array.IndexOf(ids, "core.fineResolve");
        Assert.True(iVerify >= 0 && iFine == iVerify + 1,
            $"core.fineResolve 应紧跟在 core.verifyMesh 之后（实际 verify={iVerify} fine={iFine}）");

        Flow.SelfTest();   // 顺带过一次自检：Id 唯一、命令与阶段互指
    }
}
