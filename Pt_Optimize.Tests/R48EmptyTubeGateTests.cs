using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R48 空管到温稳态（无玻璃）的快门（2026-09-14，Opus 5）。
///
/// 用户 2026-09-14：设备到温后、进玻璃前的空管保温「以稳态计算」—— 与带玻璃稳态并列、全部判据都要过的工况。
/// 病（改前）：SegmentSolver.Profile 的玻璃迎风式在产量 0 且管内玻璃换热 0 时是 0/0，源项 0 × NaN = NaN，
///   Bvp1D 见 NaN 保留旧值、误差记 0 ⇒ 金属场停在初值（全段 = 设定温度）并报收敛 —— 一张看起来正常的错表。
///   （走读得出，未在改前代码上实跑复现。）
///
/// 本档的门：
///   1. 带玻璃**逐位不变**：三个算例（开箱默认反算电流／带两端抽热＋邻段＋端部渐变保温的定电流／hg = 0 但有流量）的结果指纹
///      与改动前（基线树 .basetree 的 SegmentSolver，本档同一段指纹代码 2026-09-14 跑出）逐位相同。
///      2026-09-14 Opus 5（审查修改轮）：新加两个钩子（端部保温形状 hg、管腔辐射 kA）与析晶不适用之后，同三个指纹仍须逐位相同 —— 记录值不改。
///   2. 单段空管：无 NaN（金属）、玻璃各量与析晶裕度记 NaN 且标出工况、中点 = 设定温度、焦耳热 = 散热（能量账调生产的 SegmentSolver.EnergyBalance）。
///   3. 空管不读玻璃进口：玻璃进口给 NaN（整线串联时空管段拿到的就是它）与给 1150 结果逐位相同。
///   4. 钩子：端部额外保温的形状两工况相同（审查第 5 条）；管腔辐射 kA 钩子默认逐位不变、设了才起作用（审查第 3 条）；热偶读数基准（审查第 2 条）。
///   5. 整线双工况（两段三片，开箱网格，同一设计各跑一次带玻璃与空管）：工况说明写出控温点与来源、玻璃温降标「判不了」并写不适用、判据不缺席、
///      Base 没被改；**「升温」「法兰截面 J」两工况逐位相同**（审查第 4 条：尺寸链与工况无关，此前「已实证」的证据是空转的）；
///      **无法兰基线两工况不同**（审查第 8 条：证明基线真按空管算，不是读 Base 或复用带玻璃缓存）。
///   6. 整线空管的端部保温形状经 LineRunner 写成带玻璃形状（审查第 5 条的接线门）。
///   R48 G3（2026-09-15，Opus 5）：管腔轴向辐射由钩子改为模型输入（DesignInputs.TubeCavityRadKA1150WmPerK，空管才有、默认 0.023）——
///     第 4 条改成「带玻璃给多少都逐位不变、空管给了才起作用」；整线双工况的空管那一份从此含管腔辐射（断言都是定性的，未改门槛）；
///     三个指纹记录值不改。管腔辐射本身的门见 R48CavityRadiationGateTests。
/// </summary>
public class R48EmptyTubeGateTests
{
    private readonly ITestOutputHelper _o;
    public R48EmptyTubeGateTests(ITestOutputHelper o) => _o = o;

    /// <summary>结果指纹：金属与玻璃温度剖面、电流及玻璃相关的各项标量的逐位 FNV-1a。</summary>
    internal static ulong Fingerprint(SolveResult r)   // R48 G3（2026-09-15，Opus 5）：internal —— 管腔辐射门调同一份指纹代码，不另抄
    {
        ulong h = 1469598103934665603UL;
        void Add(double v)
        {
            ulong b = (ulong)BitConverter.DoubleToInt64Bits(v);
            for (int k = 0; k < 8; k++) { h ^= (b >> (8 * k)) & 0xFF; h *= 1099511628211UL; }
        }
        foreach (var v in r.TMetal) Add(v);
        foreach (var v in r.TGlass) Add(v);
        foreach (var v in new[] { r.CurrentA, r.TGlassOutC, r.TauWithGlassS, r.VelocityMmPerS, r.ResidenceS, r.PressureDropKPa,
                                  r.RGlassOhm, r.ILeakA, r.PowerTotalW, r.BetaWPerMK, r.DecayLengthMm, r.StabilityRatio,
                                  r.TMinC, r.TMaxC, r.WallDesignMm })
            Add(v);
        return h;
    }

    /// <summary>带两端抽热、左邻段、端部渐变保温 —— 让提出去的 TubeLossAt / EndFluxes 两条路都被走到。</summary>
    internal static DesignInputs CaseB() => new()
    {
        TSetC = 1150, TGlassInC = 1150,
        FlangeDrawOverrideSet = true, FlangeDrawOverrideW = 3.0,
        FlangeDrawLeftW = 5.0, FlangeDrawRightW = -2.0,
        NeighbourTempLeftC = 1100.0,
        EndInsulExtraMm = 10.0, EndInsulLengthMm = 30.0
    };

    internal static DesignInputs Empty(DesignInputs p) { p.ThroughputTPerDay = 0; p.HGlass = 0; return p; }

    /// <summary>
    /// ★ R48 M（2026-09-18，Fable 5.1）：**旧段解地板**（电流二分 0.05 A／Picard 0.01 K／Bvp1D 0.005 K）注射回去 ——
    /// 三个记录指纹是在旧地板上记的；地板降十倍之后场与电流会在第 3～4 位小数上变（那是分辨率变了，不是物理变了），
    /// 所以旧指纹改在旧地板下核（证明除地板外一位都没动），新地板下另记三个新值（见 带玻璃_逐位不变_三个算例指纹）。
    /// </summary>
    internal static DesignInputs LegacyFloor(DesignInputs p)
    {
        p.SegCurrentTolA = DesignInputs.SegCurrentTolALegacy; p.SegPicardTolK = DesignInputs.SegPicardTolKLegacy; p.SegBvpTolK = DesignInputs.SegBvpTolKLegacy;
        return p;
    }

    /// <summary>R48 M（2026-09-18，Fable 5.1）：新段解地板下三个算例的指纹记录值（管腔辐射门 R48CavityRadiationGateTests 也读这三个）。</summary>
    //   本轮实跑（2026-09-18 22:26 快套件）：新地板 A I=1817.3785245058946（旧地板 1817.3693389729033，差 0.009 A）；旧地板下三个旧值逐位相同。
    internal const ulong NewFloorFpA = 0x7A3DF51B3B6CE931UL, NewFloorFpB = 0x59133E0A112F3092UL, NewFloorFpC = 0x478D61917F1886FDUL;

    [Fact]
    public void 带玻璃_逐位不变_三个算例指纹()
    {
        // 记录值出处：改动前的 SegmentSolver（与 .basetree 同），本档同一段 Fingerprint 代码，2026-09-14 Opus 5 跑两次逐位相同：
        //   A I=1817.3693389729033 tg=1299.9999999999989　B tm0=1102.8710817309147 tg=1144.9589098760725　C tm0=916.7976616835206 tg=1300
        //   审查修改轮（2026-09-14 Opus 5）加了 AxialKA／EndInsulShapeHGlass／析晶不适用，三个值未改（若红 = 带玻璃逐位变了，不许改数凑绿）。
        // ★★★★★ R48 M（2026-09-18，Fable 5.1，**改门写明变因**）：段解地板降了十倍（DesignInputs.Seg*Default；出处见 DesignInputs 那段注记）⇒
        //   同三个算例在**新地板**下场与电流在第 3～4 位小数上变。处置不是改数凑绿，而是**两层各核各的**：
        //   ① 旧地板注射回去（LegacyFloor）⇒ 三个旧记录值必须逐位相同（证明除地板外一位都没动）；
        //   ② 新地板（生产默认）⇒ 另记三个新值（本轮实跑两次逐位相同才记）。
        var aL = SegmentSolver.Solve(LegacyFloor(new DesignInputs()));
        var bL = SegmentSolver.SolveAtCurrent(LegacyFloor(CaseB()), 1500);
        var cL = SegmentSolver.SolveAtCurrent(LegacyFloor(new DesignInputs { HGlass = 0, TSetC = 1300 }), 1400);
        _o.WriteLine($"旧地板：A fp=0x{Fingerprint(aL):X16}　B fp=0x{Fingerprint(bL):X16}　C fp=0x{Fingerprint(cL):X16}");
        Assert.Equal(0xC7FC3FCBD3F4E3E9UL, Fingerprint(aL));
        Assert.Equal(0x51513712C997642EUL, Fingerprint(bL));
        Assert.Equal(0x81B62A725D0769CDUL, Fingerprint(cL));

        var a = SegmentSolver.Solve(new DesignInputs());
        var b = SegmentSolver.SolveAtCurrent(CaseB(), 1500);
        var c = SegmentSolver.SolveAtCurrent(new DesignInputs { HGlass = 0, TSetC = 1300 }, 1400);
        _o.WriteLine($"新地板：A fp=0x{Fingerprint(a):X16}　B fp=0x{Fingerprint(b):X16}　C fp=0x{Fingerprint(c):X16}　I={a.CurrentA:R}（旧 {aL.CurrentA:R}）");
        Assert.True(a.Ok && b.Ok && c.Ok);
        Assert.False(a.EmptyTube || b.EmptyTube || c.EmptyTube, "带玻璃（或只 hg = 0 有流量）的算例被判成了空管");
        Assert.Equal("", a.Note);
        Assert.NotEqual(Fingerprint(aL), Fingerprint(a));   // 新地板真的接进段解了（否则下面三个新值就是旧值的复印）
        // ★★ 2026-09-18 Opus 5（合并 H×合并树，**重录并写明变因**）：三个记录值由
        //   A 0x33B7908F6563A1E0 / B 0x20CA37AA8C454479 / C 0x02DA1FDB755FFD7A
        //   改为 A 0xC7FC3FCBD3F4E3E9 / B 0x51513712C997642E / C 0x81B62A725D0769CD。
        //   变因**实测定死，不是推断**：把合并前那棵树（index tree 0d8ffa36）单独解出来建了一次，跑同三个算例逐字段打印，与合并后逐字段比 ——
        //   进指纹的 17 项里**只有 TauWithGlassS 一项变**（A 250.62889616403336→252.90073533709173、B 273.6007727193096→275.783820869701、
        //   C 398.41245143873607→402.02388263468384；同时 TauMetalS 也变但它不进指纹），TMetal／TGlass 全部 401 点、CurrentA、TGlassOutC、
        //   PowerTotalW、BetaWPerMK、DecayLengthMm、StabilityRatio、TMinC、TMaxC、WallDesignMm **逐位相同**。
        //   唯一变因 = H 路把纯铂比热由旧式 133.0 + 0.0135·t 改成 Kaye & Laby 四点最小二乘（Materials.CpFitA/CpFitB，见 HANDOVER §0.-6H），
        //   而 TauWithGlassS = (cMetal + cGlass)/β、cMetal ∝ PtCp(TSetC)。⇒ **带玻璃稳态场一位都没动**，动的只有显示用的时间常数。
        // 新地板下的三个记录值（R48 M 2026-09-18 Fable 5.1 本轮实跑，连跑两次逐位相同）
        Assert.Equal(NewFloorFpA, Fingerprint(a));
        Assert.Equal(NewFloorFpB, Fingerprint(b));
        Assert.Equal(NewFloorFpC, Fingerprint(c));
        Assert.True(double.IsFinite(a.DevitMarginMinK), "带玻璃的析晶裕度应照常出数");
    }

    [Fact]
    public void 空管单段_无NaN_中点到设定_焦耳热等于散热()
    {
        var p = Empty(new DesignInputs());
        Assert.True(SegmentSolver.IsEmptyTube(p));
        var r = SegmentSolver.Solve(p);
        Assert.True(r.Ok, r.Message);
        Assert.True(r.EmptyTube);
        // R48 G3 审查后（2026-09-15，Opus 5）：默认系数 0.023 > 0 ⇒ 说明 = 原「不适用」那句 ＋ 管腔辐射一句（全文调生产函数 EmptyTubeNoteOf，不手抄）
        Assert.Equal(SegmentSolver.EmptyTubeNoteOf(p), r.Note);
        Assert.StartsWith(SegmentSolver.EmptyTubeNote, r.Note);
        Assert.Contains("管腔内轴向辐射", r.Note);

        Assert.All(r.TMetal, t => Assert.True(double.IsFinite(t), "空管金属温度出现 NaN/∞"));
        Assert.True(double.IsFinite(r.CurrentA) && r.CurrentA > 1, $"电流 {r.CurrentA}");
        Assert.All(r.TGlass, t => Assert.True(double.IsNaN(t), "空管的玻璃温度应记 NaN（不适用），不许是数"));
        foreach (var (name, v) in new[] { ("玻璃出口", r.TGlassOutC), ("含玻璃时间常数", r.TauWithGlassS), ("流速", r.VelocityMmPerS),
                                          ("停留时间", r.ResidenceS), ("压降", r.PressureDropKPa), ("流动水头", r.GlassHeadM),
                                          ("玻璃电阻", r.RGlassOhm), ("漏电", r.ILeakA), ("溶铂", r.PtDissolvedGPerH),
                                          ("析晶裕度", r.DevitMarginMinK) })
            Assert.True(double.IsNaN(v), $"空管的「{name}」应记 NaN（不适用），实际 {v}");
        Assert.False(r.DevitRisk, "空管没有玻璃，析晶风险位应为 false（审查第 6 条）");
        Assert.Contains("析晶", r.Note);
        Assert.True(double.IsFinite(r.BetaWPerMK) && double.IsFinite(r.DecayLengthMm) && double.IsFinite(r.TauMetalS));

        int mid = r.TMetal.Length / 2;
        Assert.InRange(r.TMetal[mid], p.TSetC - 0.5, p.TSetC + 0.5);   // 与 VerificationTests.Segment_CurrentSatisfiesSetpointAndJConstraint 同一容差

        var e = SegmentSolver.EnergyBalance(p, r);
        _o.WriteLine($"空管单段：I={r.CurrentA:0.0} A　中点 {r.TMetal[mid]:0.000} °C　焦耳热 {e.JouleW:0.00} W　散热 {e.SurfaceLossW:0.00} W　"
                   + $"给玻璃 {e.ToGlassW:0.00} W　两端流出 {e.EndsOutW:0.00} W　残差 {e.ResidualRel:P3}");
        Assert.Equal(0.0, e.ToGlassW);
        Assert.Equal(0.0, e.EndsOutW);
        Assert.True(Math.Abs(e.JouleW - e.SurfaceLossW) <= 0.005 * e.JouleW,
            $"空管焦耳热 {e.JouleW:0.00} W ≠ 散热 {e.SurfaceLossW:0.00} W（差 {(e.JouleW - e.SurfaceLossW) / e.JouleW:P3}，限 0.5 %）");

        // 改前的病会怎样漏过：金属停在初值、电流随便停在哪 —— 焦耳热与散热对不上。上面那条就是守它的。
        // ⚠ 开箱单段两端绝热、散热沿长均匀 ⇒ 真解本来就是全段 = 设定温度，剖面「平」不说明什么。
        //   再加一个剖面不平的空管：两端抽热＋左邻段，反算电流 —— 中点仍要到设定，账里两端流出项非零也要平。
        //   ⚠ 不带端部额外保温：空管＋端部额外保温在反算电流时，上界 0.9·I_stab 那一点的剖面跑出散热表范围、被夹成负的垃圾值，
        //     FindCurrent 抛「到不了设定温度」—— 既有的括根脆弱，本路没修，数与出处见 R48EmptyTubeStateTests.端部额外保温_空管单段探针。
        var pd = Empty(CaseB()); pd.EndInsulExtraMm = 0;
        var rd = SegmentSolver.Solve(pd);
        Assert.True(rd.Ok, rd.Message);
        Assert.All(rd.TMetal, t => Assert.True(double.IsFinite(t)));
        Assert.InRange(rd.TMetal[rd.TMetal.Length / 2], pd.TSetC - 0.5, pd.TSetC + 0.5);
        Assert.True(rd.TMaxC - rd.TMinC > 1.0, $"带抽热的空管剖面应不平（实际落差 {rd.TMaxC - rd.TMinC:0.000} K）");
        var ed = SegmentSolver.EnergyBalance(pd, rd);
        _o.WriteLine($"空管带抽热：I={rd.CurrentA:0.0} A　焦耳热 {ed.JouleW:0.00} W　散热 {ed.SurfaceLossW:0.00} W　两端流出 {ed.EndsOutW:0.00} W　残差 {ed.ResidualRel:P3}　落差 {rd.TMaxC - rd.TMinC:0.00} K");
        Assert.True(Math.Abs(ed.EndsOutW) > 1.0, "两端流出项应非零（不然没检到端部项）");
        Assert.True(Math.Abs(ed.ResidualRel) <= 0.005, $"空管带抽热能量账不平：残差 {ed.ResidualRel:P3}");

        // 正对照：带玻璃、带两端抽热＋邻段＋端部保温的定电流算例，同一本账（含给玻璃与两端项）也要平。
        var pb = CaseB(); var rb = SegmentSolver.SolveAtCurrent(pb, 1500);
        var eb = SegmentSolver.EnergyBalance(pb, rb);
        _o.WriteLine($"正对照（带玻璃）：焦耳热 {eb.JouleW:0.00} W　散热 {eb.SurfaceLossW:0.00} W　给玻璃 {eb.ToGlassW:0.00} W　两端流出 {eb.EndsOutW:0.00} W　残差 {eb.ResidualRel:P3}");
        Assert.True(Math.Abs(eb.ResidualRel) <= 0.005, $"带玻璃算例能量账不平：残差 {eb.ResidualRel:P3}");
        Assert.True(Math.Abs(eb.ToGlassW) > 1.0, "正对照的给玻璃一项应非零（不然这本账没检到玻璃项）");
    }

    [Fact]
    public void 空管不读玻璃进口_NaN与1150逐位相同()
    {
        var p1 = Empty(CaseB()); p1.TGlassInC = double.NaN;
        var p2 = Empty(CaseB()); p2.TGlassInC = 1150;
        var r1 = SegmentSolver.SolveAtCurrent(p1, 1500);
        var r2 = SegmentSolver.SolveAtCurrent(p2, 1500);
        Assert.True(r1.Ok && r2.Ok, r1.Message + r2.Message);
        Assert.All(r1.TMetal, t => Assert.True(double.IsFinite(t)));
        Assert.Equal(Fingerprint(r2), Fingerprint(r1));
        // 与带玻璃的同一算例确实不同（不然上面那条等于没检）
        Assert.NotEqual(SegmentSolver.SolveAtCurrent(CaseB(), 1500).TMetal[0], r1.TMetal[0]);
    }

    [Fact]
    public void 钩子_端部保温形状两工况相同_管腔辐射带玻璃逐位不变_热偶基准()   // R48 G3 审查后（2026-09-15，Opus 5）改名：原「…默认逐位不变…」—— 默认已是 0.023，测的是带玻璃给多少都不变
    {
        // ── 审查第 5 条：端部额外保温是硬件，形状按带玻璃的 hg 定。
        //   带玻璃：设定 1080、玻璃进口 1150（损失表上界 max(1080,1150)+400）；空管：同一段把产量、hg 置 0（上界 1080+400）。
        var g = new DesignInputs { TSetC = 1080, TGlassInC = 1150, EndInsulExtraMm = 10, EndInsulLengthMm = 30 };
        var eOwn = Empty(SegmentSolver.Clone(g));                                    // 不写形状 hg（审查前的行为：形状跟着 hg = 0 变）
        var eHw = Empty(SegmentSolver.Clone(g)); eHw.EndInsulShapeHGlass = g.HGlass;  // LineRunner 空管段的写法
        double wall = g.WallMinMm;
        var pg = SegmentSolver.EndInsulExtraProfileMm(g, wall);
        var po = SegmentSolver.EndInsulExtraProfileMm(eOwn, wall);
        var ph = SegmentSolver.EndInsulExtraProfileMm(eHw, wall);
        _o.WriteLine($"端部额外保温分布 mm（自端部起 {SegmentSolver.EndInsulZones} 格）：带玻璃 {string.Join("/", pg.Select(v => v.ToString("0.0000")))}　"
                   + $"空管·按带玻璃 hg {string.Join("/", ph.Select(v => v.ToString("0.0000")))}　空管·按自身 hg=0 {string.Join("/", po.Select(v => v.ToString("0.0000")))}");
        Assert.Equal(SegmentSolver.EndInsulZones, pg.Length);
        for (int z = 0; z < pg.Length; z++)
            Assert.True(Math.Abs(ph[z] - pg[z]) <= 1e-3 * pg[z],
                $"第 {z} 格：空管按带玻璃 hg 的形状 {ph[z]:0.00000} 与带玻璃 {pg[z]:0.00000} 差超过 0.1 %（只该差损失表样条插值）");
        Assert.True(po[^1] > 1.2 * pg[^1], $"正对照：按自身 hg=0 算的最外格 {po[^1]:0.000} 应明显比带玻璃 {pg[^1]:0.000} 厚 —— 不然本门没检到病");
        Assert.Empty(SegmentSolver.EndInsulExtraProfileMm(new DesignInputs(), wall));   // 开箱没有端部额外保温

        // ── 审查第 3 条：管腔辐射。R48 G3（2026-09-15，Opus 5）：钩子改成模型输入（空管才有，默认低档 0.023）——
        //   带玻璃时系数给 0 或给高档，结果逐位相同（管腔被玻璃占满，没有这条路）；空管时给了才改变剖面，且能量账仍平。
        var a0 = SegmentSolver.SolveAtCurrent(CaseB(), 1500);
        var pb = CaseB(); pb.TubeCavityRadKA1150WmPerK = 0.0;
        var a1 = SegmentSolver.SolveAtCurrent(pb, 1500);
        var pb2 = CaseB(); pb2.TubeCavityRadKA1150WmPerK = SegmentSolver.CavityRadKA1150High;
        var a2 = SegmentSolver.SolveAtCurrent(pb2, 1500);
        Assert.Equal(Fingerprint(a0), Fingerprint(a1));
        Assert.Equal(Fingerprint(a0), Fingerprint(a2));
        Assert.Equal(0.0, a2.CavityRadKAWmPerK);
        var pr = Empty(CaseB()); pr.EndInsulExtraMm = 0; pr.TubeCavityRadKA1150WmPerK = 0.023;
        var pn = Empty(CaseB()); pn.EndInsulExtraMm = 0; pn.TubeCavityRadKA1150WmPerK = 0.0;
        var rr = SegmentSolver.SolveAtCurrent(pr, 1500); var rn = SegmentSolver.SolveAtCurrent(pn, 1500);
        Assert.True(rr.Ok && rn.Ok, rr.Message + rn.Message);
        _o.WriteLine($"管腔辐射（空管、带抽热、左邻段 1100 °C、定电流 1500 A）：A 端 {rn.TMetal[0]:0.00} → {rr.TMetal[0]:0.00} °C　热衰减长度 {rn.DecayLengthMm:0.0} → {rr.DecayLengthMm:0.0} mm　段内 kA {rr.CavityRadKAWmPerK:0.0000}");
        Assert.True(Math.Abs(rr.TMetal[0] - rn.TMetal[0]) > 0.1, "设了管腔辐射却不改剖面 —— 没接上");
        Assert.True(rr.DecayLengthMm > rn.DecayLengthMm);
        Assert.Equal(0.023, rr.CavityRadKAWmPerK);   // 控温点 1150 = 标定温度 ⇒ 换算系数恰为 1
        var er = SegmentSolver.EnergyBalance(pr, rr);
        Assert.True(Math.Abs(er.ResidualRel) <= 0.005, $"带管腔辐射的能量账不平：残差 {er.ResidualRel:P3}");

        // ── 审查第 2 条：热偶读数基准（用户 2026-09-14：共用片取两侧热偶读数的对数平均）。
        //   记录值出处：awk 手算 (1423.15−1353.15)/ln(1423.15/1353.15) − 273.15 = 1114.7058；(1353.15−1323.15)/ln(1353.15/1323.15) − 273.15 = 1064.9440（2026-09-14 Opus 5）
        var sp = new[] { 1150.0, 1080.0, 1050.0 };
        Assert.Equal(1150.0, ThermocoupleBasis.ReferenceC(sp, 0));
        Assert.Equal(1050.0, ThermocoupleBasis.ReferenceC(sp, 3));
        Assert.InRange(ThermocoupleBasis.ReferenceC(sp, 1), 1114.7058 - 1e-3, 1114.7058 + 1e-3);
        Assert.InRange(ThermocoupleBasis.ReferenceC(sp, 2), 1064.9440 - 1e-3, 1064.9440 + 1e-3);
        Assert.True(ThermocoupleBasis.ReferenceC(sp, 1) < 0.5 * (1150 + 1080), "对数平均应低于算术平均");
        Assert.Equal(1150.0, ThermocoupleBasis.LogMeanC(1150, 1150));
        Assert.True(double.IsNaN(ThermocoupleBasis.ReferenceC(sp, 4)));
    }

    [Fact]
    public void 整线双工况_工况说明_尺寸链逐位相同_基线按空管算()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 };          // 与 DesignCurrentTests 同：两段三片，开箱网格（快）
        d.SegLengthMm = new[] { 300.0, 300.0 };
        d = d.Fit();

        // ── 造算例：默认带玻璃；空管默认按升温目标全线（审查第 1 条），AsGiven 沿用设计控温点
        var lcG = d.BuildCase(p, checkRamp: false);
        Assert.False(lcG.EmptyTube, "BuildCase 默认应是带玻璃稳态");
        var lcR = d.BuildCase(p, checkRamp: false, emptyTube: true);
        Assert.True(lcR.EmptyTube);
        Assert.Equal(EmptyTubeSetpoint.RampTarget, lcR.EmptyTubeSetpointFrom);
        Assert.All(lcR.SetpointC, v => Assert.Equal(lcR.RampTargetC, v));
        Assert.Equal(new[] { 1150.0, 1080.0 }, d.SetpointC);                     // 设计记录的控温点没被改
        Assert.Contains($"全线取升温目标 {lcR.RampTargetC:0.#} °C", LineRunner.EmptyTubeNoteFor(lcR));
        var lc = d.BuildCase(p, checkRamp: false, emptyTube: true, emptyTubeSetpoint: EmptyTubeSetpoint.AsGiven);
        Assert.Equal(EmptyTubeSetpoint.AsGiven, lc.EmptyTubeSetpointFrom);
        Assert.Equal(lcG.SetpointC, lc.SetpointC);        // 下面的两工况对照只差工况位
        double thr0 = lc.Base.ThroughputTPerDay, hg0 = lc.Base.HGlass;

        var rg = LineRunner.Run(lcG);
        var r = LineRunner.Run(lc);
        Assert.True(rg.Ok, rg.Message);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(LineRunner.EmptyTubeNoteFor(lc), r.Notes[0]);
        Assert.DoesNotContain(rg.Notes, n => n.StartsWith("工况：空管", StringComparison.Ordinal));
        foreach (var must in new[] { "控温点 1150/1080 °C", "管腔内轴向辐射", "管口辐射散热", "许用应力按段设定温度取" })
            Assert.Contains(must, r.Notes[0]);
        Assert.DoesNotContain("偏保守", r.Notes[0]);    // 审查第 7 条：没有依据说管强度偏保守
        Assert.Equal(thr0, lc.Base.ThroughputTPerDay);     // 段解用的是克隆，Base 不许被改（设计电流链读 Base）
        Assert.Equal(hg0, lc.Base.HGlass);

        foreach (var s in r.Segments)
        {
            Assert.True(double.IsFinite(s.CurrentA) && s.CurrentA > 1, $"{s.Name} 电流 {s.CurrentA}");
            Assert.True(double.IsFinite(s.TRootAC) && double.IsFinite(s.TRootBC), $"{s.Name} 管根 NaN");
            Assert.True(double.IsFinite(s.BaseTRootAC) && double.IsFinite(s.BaseTRootBC), $"{s.Name} 无法兰基线 NaN");
            Assert.True(double.IsNaN(s.GlassInC) && double.IsNaN(s.GlassOutC), $"{s.Name} 玻璃进出口应记 NaN（不适用）");
            Assert.All(s.TGlass, t => Assert.True(double.IsNaN(t)));
        }
        Assert.All(r.Flanges, f => Assert.True(double.IsFinite(f.QFromTubeW) && double.IsFinite(f.TMaxC), $"{f.Name} 抽热/峰值 NaN"));

        var gd = r.Find(LineResult.Key.GlassDrop);
        Assert.NotNull(gd);
        Assert.True(gd!.Undetermined, "空管的玻璃温降那条应标「判不了」（不适用）");
        Assert.Equal(CheckKind.Reference, gd.Kind);
        Assert.Contains("不适用", gd.Note);
        Assert.True(double.IsNaN(r.GlassDropModelK));
        Assert.Empty(r.MissingChecks);
        // 判定用的判据：凡是没标「判不了」的，数必须是数（玻璃的 NaN 不许流进别的判据）
        foreach (var c in r.Checks.Where(c => c.Kind != CheckKind.Reference && !c.Undetermined))
            Assert.True(double.IsFinite(c.Actual), $"{c.Name} 的实际值 {c.Actual} 不是数");

        // ── 审查第 4 条：尺寸链与工况无关 ⇒「升温」「法兰截面 J」的实际值两工况**逐位**相同（同一设计、同一控温点，只差工况位）。
        foreach (var key in new[] { LineResult.Key.Ramp, LineResult.Key.SectionJ })
        {
            var a = rg.Find(key); var b = r.Find(key);
            Assert.True(a is not null && b is not null, $"{key} 缺席");
            Assert.True(double.IsFinite(a!.Actual), $"{key} 带玻璃实际值 {a.Actual}");
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Actual), BitConverter.DoubleToInt64Bits(b!.Actual));
        }
        //   反过来：取本次稳态场的参考量随工况变，空管结果里要标明（带玻璃不标，逐位不变）。
        var fg = rg.Find(LineResult.Key.RampField); var fe = r.Find(LineResult.Key.RampField);
        Assert.True(fg is not null && fe is not null, "升温期法兰−管峰值 缺席");
        Assert.Contains("空管到温稳态", fe!.Note);
        Assert.DoesNotContain("空管到温稳态", fg!.Note);
        Assert.Contains("两节点对照电流取自本次空管到温稳态场", r.Find(LineResult.Key.SectionJ)!.Note);
        _o.WriteLine($"升温期法兰−管峰值（参考量，随工况变）：带玻璃 {fg.Actual:0.000} K　空管 {fe.Actual:0.000} K");

        // ── 审查第 8 条：无法兰基线真按空管算 —— 与同一设计、同一控温点的带玻璃基线必须不同。
        //   若有人把基线改成读 Base（带玻璃）或复用带玻璃的缓存，两边会相同，这里就红。
        double maxDiff = 0;
        for (int i = 0; i < r.Segments.Length; i++)
            maxDiff = Math.Max(maxDiff, Math.Max(Math.Abs(r.Segments[i].BaseTRootAC - rg.Segments[i].BaseTRootAC),
                                                 Math.Abs(r.Segments[i].BaseTRootBC - rg.Segments[i].BaseTRootBC)));
        _o.WriteLine($"无法兰基线两工况最大差 {maxDiff:0.000} K；各段 A/B：带玻璃 "
                   + string.Join("  ", rg.Segments.Select(s => $"{s.BaseTRootAC:0.00}/{s.BaseTRootBC:0.00}"))
                   + "　空管 " + string.Join("  ", r.Segments.Select(s => $"{s.BaseTRootAC:0.00}/{s.BaseTRootBC:0.00}")));
        Assert.True(maxDiff > 0.5, $"空管与带玻璃的无法兰基线几乎相同（最大差 {maxDiff:0.000} K）—— 基线没按空管算");
        for (int i = 0; i < r.Segments.Length; i++)     // 回写的缓存就是本工况的基线
        {
            Assert.Equal(r.Segments[i].BaseTRootAC, lc.BaselineRootC[i][0]);
            Assert.Equal(r.Segments[i].BaseTRootBC, lc.BaselineRootC[i][1]);
        }

        _o.WriteLine($"整线空管：收敛 {r.Converged}　电流 {string.Join("/", r.Segments.Select(s => s.CurrentA.ToString("0")))} A　"
                   + $"抽热 {string.Join("/", r.Flanges.Select(f => f.QFromTubeW.ToString("+0.00;-0.00")))} W");
    }

    [Fact]
    public void 整线空管_端部保温形状经LineRunner写成带玻璃形状()
    {
        // 审查第 5 条的接线门（新状态位默认没接上）：LineRunner 空管段要把置 0 之前的 hg 写进形状钩子。
        //   读 SegmentOut.EndInsulExtraProfileMm（段解同一份参数算的），两工况逐格比。
        //   形状只看段参数、与电流和耦合是否收敛无关 ⇒ 按给定电流、只跑 2 轮耦合（反算电流＋收敛要 5 分半，2026-09-14 实测，放不进快套件）。
        var p = new DesignInputs { EndInsulExtraMm = 5, EndInsulLengthMm = 30 };
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 };
        d.SegLengthMm = new[] { 300.0, 300.0 };
        d = d.Fit();
        LineCase Quick(LineCase c) { c.UseMeasuredCurrent = true; c.MeasuredCurrentA = new[] { 1200.0, 1150.0 }; c.CoupleMaxRounds = 2; return c; }
        var rg = LineRunner.Run(Quick(d.BuildCase(p, checkRamp: false)));
        var re = LineRunner.Run(Quick(d.BuildCase(p, checkRamp: false, emptyTube: true, emptyTubeSetpoint: EmptyTubeSetpoint.AsGiven)));
        Assert.True(rg.Ok, rg.Message);
        Assert.True(re.Ok, re.Message);
        for (int i = 0; i < rg.Segments.Length; i++)
        {
            var a = rg.Segments[i].EndInsulExtraProfileMm; var b = re.Segments[i].EndInsulExtraProfileMm;
            _o.WriteLine($"{rg.Segments[i].Name} 端部额外保温 mm：带玻璃 {string.Join("/", a.Select(v => v.ToString("0.0000")))}　空管 {string.Join("/", b.Select(v => v.ToString("0.0000")))}");
            Assert.Equal(SegmentSolver.EndInsulZones, a.Length);
            Assert.Equal(a.Length, b.Length);
            for (int z = 0; z < a.Length; z++)
                Assert.True(Math.Abs(a[z] - b[z]) <= 1e-3 * a[z], $"{rg.Segments[i].Name} 第 {z} 格：空管 {b[z]:0.00000} ≠ 带玻璃 {a[z]:0.00000} —— 形状随工况变了");
        }
    }
}
