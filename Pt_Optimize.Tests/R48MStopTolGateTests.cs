using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 M 路快门：**停机容差改成绝对目标 ＋ 认证误差交下游 ＋ 求解器第三状态位「判不了」＋ 格点判决走格 ＋ 放大口径** —— 2026-09-18，Fable 5.1
//
//  病（出处 r48_U/deliverable/R48_U_不收敛点诊断_总表_本次开跑于2026-09-18_163414.txt，只读）：
//    ① 容差 = 0.1 × 当前最小硬安全线裕度 ⇒ 二分逼近「裕度 = 0」的根时容差撞下限 0.02 K ⇒ 真残差要压到 6e−4 K，低于段解地板 ⇒ 轮数彩票（98／591 轮）；
//    ② Solver.Rounds 里任何 bad 一律 res.HitBound = true，而 HitBound 的定义是「旋钮到顶 ⇒ 不可行的证明」⇒ 报告印「不可行」「再算一次会得到同一句话」、
//       界面把人推去「搜形状」—— 三处与事实不符（同一点实测重跑两次都收敛）。
//
//  这几道门守的是：
//    ① 容差是绝对目标（生产默认不按裕度走；「按裕度」只剩注射用）；
//    ② 认证误差 = 放大 × 停机残差 随结果交下游（LineResult.CertErrK），报告、证据头、求解器格点判决都读它；
//    ③ 放大 = max(闭式 × 1.1, 实测雅可比)，rEst/(1−rEst) 那一支删了；
//    ④ 裕度小于认证误差的硬安全线判不了那么细（判不了不算过），只认温度判据；
//    ⑤ 第三状态位：三条出口置 Undetermined 不置 HitBound；判词「判不了（…）」；「再算一次会得到同一句话」只许挂在 HitBound；
//    ⑥ 界面：判不了不推去搜形状，指路说「这一点没解到收敛：可加轮数上限／细化网格重算」；位在 PushFlow 发布、参数一动就清；
//    ⑦ 二分在图纸格点上走、括号宽 ≤ 一格即停；格点上裕度小于认证误差 ⇒ 走格（纯函数当场验）。
//  慢门：R48MStopTolTwoPointTests（两点各 ≤120 轮、注射 ⇒ 13.074 超 300 轮）、R48MStopTolCostTests（成本与四行对拍）。
// ════════════════════════════════════════════════════════════════════════════

public class R48MStopTolGateTests
{
    private static string Code(string rel)
        => File.ReadAllText(Path.Combine(HandoverDoc.Root(), rel.Replace('/', Path.DirectorySeparatorChar)));

    private static ConstraintOut Hard(string key, double actual, double limit, string unit = "K", bool lessIsBetter = true) => new()
    {
        Name = key, Unit = unit, Kind = CheckKind.HardSafety, Actual = actual, Limit = limit, LessIsBetter = lessIsBetter,
        Ok = lessIsBetter ? actual <= limit : actual >= limit,
    };

    // ────────────────────────────────────────────────────────────────────
    //  ① 容差是绝对目标
    // ────────────────────────────────────────────────────────────────────

    /// <summary>生产默认不随裕度走；把开关打开（注射）才跟着裕度撞下限。</summary>
    [Fact]
    public void 门_停机容差是绝对目标_默认不按裕度走_注射才按裕度()
    {
        var lc = new LineCase();
        Assert.False(lc.CoupleTolFromMargin, "生产默认必须是绝对目标（R48 M 2026-09-18）");
        var nearRoot = new LineResult { Checks = new[] { Hard(LineResult.Key.ColdUnderTc, 4.99, 5.0) } };   // 裕度 0.01 K
        Assert.Equal(lc.CoupleTolK, LineRunner.CoupleTolKFor(lc, nearRoot), 12);        // 不随裕度走
        Assert.Equal(lc.CoupleTolK, LineRunner.CoupleTolKFor(lc, null), 12);

        // 注射：§0.-10 的口径放回去 ⇒ 根附近撞下限（那正是轮数彩票的来源）
        var inj = new LineCase { CoupleTolFromMargin = true, CoupleTolK = 0.1 };
        Assert.Equal(inj.CoupleTolFloorK, LineRunner.CoupleTolKFor(inj, nearRoot), 12);
        Assert.True(LineRunner.CoupleTolKFor(inj, nearRoot) < lc.CoupleTolK, "注射后的容差不比生产默认细 —— 注射没注进去");

        // 生产默认的量级：不粗于 §0.-10 的可复现门 0.05 K（摆幅 ≈ 0.6～0.75 × 剩余误差，见 §0.-10 ②）
        Assert.True(lc.CoupleTolK <= 0.05 + 1e-12, $"生产默认 {lc.CoupleTolK} K 比可复现门 0.05 K 还粗");
        Assert.True(lc.CoupleTolK > lc.CoupleTolFloorK, "绝对目标必须高于旧下限 0.02 K —— 低于它就回到「真残差要压到段解地板以下」那个彩票");
    }

    // ────────────────────────────────────────────────────────────────────
    //  ② 认证误差交下游
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void 门_认证误差随结果交下游_只在收敛时有数_下游三处都读它()
    {
        var r = new LineResult { Converged = true, CoupleRemainK = 0.031 };
        Assert.Equal(0.031, r.CertErrK, 12);
        r.Converged = false;
        Assert.True(double.IsNaN(r.CertErrK), "未收敛时表内每个数都不可引用，认证误差必须是 NaN");

        // 下游：三关结论、求解器格点判决、整线结果的判不了标记（改回不读 ⇒ 红）
        Assert.Contains("CertErrTag(r)", Code("Pt_Optimize/Core/FinalCheckReport.cs"));
        string sv = Code("Pt_Optimize/Core/Solver.cs");
        Assert.Contains("double need = CertNeed(last, key);", sv);
        Assert.Contains("if (sl < need)", sv);
        Assert.Contains("MarkUndeterminedIfInsideCertErr(res)", Code("Pt_Optimize/Core/LineRunner.cs"));

        var rr = new LineResult { Converged = true, CoupleRemainK = 0.031, CoupleAmpUsed = 36.6 };
        Assert.Contains("认证误差 0.031 K", FinalCheckReport.CertErrTag(rr));
        Assert.Equal("", FinalCheckReport.CertErrTag(new LineResult()));
    }

    /// <summary>认证误差只对温度判据；瓦的那条是 0（与温度容差之间没有实测灵敏度，§0.-10 的理由）。</summary>
    [Fact]
    public void 门_认证误差只对温度判据_瓦的那条是0()
    {
        var r = new LineResult { Converged = true, CoupleRemainK = 0.04 };
        Assert.Equal(0.04, Solver.CertNeed(r, LineResult.Key.ColdUnderTc), 12);
        Assert.Equal(0.04, Solver.CertNeed(r, LineResult.Key.HotOverTc), 12);
        Assert.Equal(0.0, Solver.CertNeed(r, LineResult.Key.NetFlux), 12);
        Assert.Equal(0.0, Solver.CertNeed(null, LineResult.Key.ColdUnderTc), 12);
        Assert.Equal(0.0, Solver.CertNeed(new LineResult { Converged = false, CoupleRemainK = 0.04 }, LineResult.Key.ColdUnderTc), 12);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ③ 放大口径
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void 门_放大是闭式乘1点1与实测雅可比取大_rEst分支已删()
    {
        var lc = new LineCase();
        var r = new LineResult { Segments = new[] { new SegmentOut { Name = "HC1", EndAmpA = 20.0, EndAmpB = 15.0 } } };
        Assert.Equal(1.1, LineRunner.StopAmpHeadroom, 12);
        Assert.Equal(22.0, LineRunner.StopAmpOf(lc, r, double.NaN), 9);   // 没量到雅可比 ⇒ 闭式 × 1.1
        Assert.Equal(30.0, LineRunner.StopAmpOf(lc, r, 30.0), 9);         // 实测更大 ⇒ 实测赢
        Assert.Equal(22.0, LineRunner.StopAmpOf(lc, r, 10.0), 9);         // 实测更小 ⇒ 闭式 × 1.1 赢
        Assert.True(double.IsPositiveInfinity(LineRunner.StopAmpOf(lc, r, double.PositiveInfinity)), "奇异（+∞）不许被闭式盖掉 —— 判不出收敛才对");
        lc.EndTempAmpFromDecayLength = false;
        Assert.Equal(LineCase.FixedPointAmp, LineRunner.StopAmpOf(lc, r, 30.0), 9);   // 历史口径原样（门用它注射旧病）

        string s = Code("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("double ampWorst = StopAmpOf(c, res, ampJac);", s);
        Assert.Contains("double ampNow = StopAmpOf(c, res, ampJac);", s);
        Assert.Contains("? baseResid * StopAmpOf(c, br, jacAmp)", s);
        Assert.DoesNotContain("rEst / (1 - rEst)", s);        // rEst 分支删了（HANDOVER 数值常驻项）
        Assert.Contains("lastRemainK = Math.Max(remain, resK * ampWorst);", s);
        // 量雅可比只量一次、走缓存；两个来源与秒数进结果（新状态位默认没接上）
        Assert.Contains("c.JacobianAmpCache = ampJac;", s);
        Assert.Contains("res.CoupleAmpClosed = EndTempFixedPointAmpOf(c, res); res.CoupleAmpJacobian = ampJac; res.CoupleAmpUsed = StopAmpOf(c, res, ampJac);", s);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ④ 裕度小于认证误差 ⇒ 判不了那么细
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void 门_裕度小于认证误差的硬安全线判不了那么细_只认温度判据()
    {
        var r = new LineResult
        {
            Converged = true, CoupleRemainK = 0.05,
            Checks = new[]
            {
                Hard(LineResult.Key.ColdUnderTc, 4.97, 5.0),                    // 裕度 0.03 < 0.05 ⇒ 判不了那么细
                Hard(LineResult.Key.HotOverTc, 4.90, 5.0),                     // 裕度 0.10 ⇒ 不动
                Hard(LineResult.Key.NetFlux, 0.01, 0.0, "W", lessIsBetter: false),   // 瓦 ⇒ 不动（裕度 0.01 也不动）
                new ConstraintOut { Name = LineResult.Key.FlangeDip, Unit = "K", Kind = CheckKind.Reference, Actual = 9.99, Limit = 10.0, Ok = true },   // 参考量 ⇒ 不动
            },
        };
        Assert.True(r.Checks.All(c => c.Ok && !c.Undetermined), "造的例子一开始得全过，否则这道门证不出东西");
        var hit = LineRunner.MarkUndeterminedIfInsideCertErr(r);
        Assert.Equal(new[] { Criteria.Plain(LineResult.Key.ColdUnderTc) }, hit);
        Assert.True(r.Checks[0].Undetermined); Assert.False(r.Checks[0].Ok);
        Assert.Contains("认证误差 0.050 K", r.Checks[0].Note);
        Assert.False(r.Checks[1].Undetermined); Assert.True(r.Checks[1].Ok);
        Assert.False(r.Checks[2].Undetermined); Assert.True(r.Checks[2].Ok);
        Assert.False(r.Checks[3].Undetermined);
        Assert.False(r.AllOk, "判不了不算过 —— AllOk 必须跟着变假");
        Assert.Contains(r.Failed, f => f.Contains("无法判定"));

        // 未收敛 ⇒ 一条都不动（那时 CertErrK 是 NaN）
        var r2 = new LineResult { Converged = false, CoupleRemainK = 0.05, Checks = new[] { Hard(LineResult.Key.ColdUnderTc, 4.97, 5.0) } };
        Assert.Empty(LineRunner.MarkUndeterminedIfInsideCertErr(r2));
        Assert.False(r2.Checks[0].Undetermined);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ⑤ 第三状态位与判词
    // ────────────────────────────────────────────────────────────────────

    /// <summary>三条判不了出口（抬前／上界／二分中点）的返回都标 Undetermined；收场那一支置 Undetermined 不置 HitBound；NaN 出口同理。</summary>
    [Fact]
    public void 门_三条出口置判不了不置旋钮到顶_源码钉死()
    {
        string s = Code("Pt_Optimize/Core/Solver.cs");
        // 符号名经 nameof 拼（BranchMarksAreReachedTests 的欠账表只认「有测试**行为**断言过它」；这里是源码钉，不算还清那三笔欠账）
        foreach (var mark in new[] { "BranchMarks." + nameof(BranchMarks.UndeterminedBefore), "BranchMarks." + nameof(BranchMarks.UndeterminedAtHi), "BranchMarks." + nameof(BranchMarks.UndeterminedBisect) })
        {
            var m = Regex.Matches(s, Regex.Escape(mark) + @"\);\s*return \(false,[\s\S]{0,700}?, false, true\);");
            Assert.True(m.Count >= 1, $"{mark} 那一支的返回没有标 Undetermined=true（四元组最后一位）");
        }
        // 收场：判不了 ⇒ Undetermined，且 HitBound 明确清零（互斥）
        Assert.Contains("res.Undetermined = true; res.HitBound = false;", s);
        Assert.Contains("if (undet) firstUndetWhy ??= why;", s);
        // 逐片裕度 NaN、判据表里判不了、每轮开头场解解不出来：都置 Undetermined，不置 HitBound
        Assert.DoesNotContain("判不了不算过\";\n                            res.HitBound = true;", s.Replace("\r\n", "\n"));
        Assert.Contains("res.StopWhy = $\"第 {j} 片的「{Criteria.Plain(key)}」**判不了**（值是 NaN）—— 判不了不算过\";\n                            res.Undetermined = true;", s.Replace("\r\n", "\n"));
        Assert.Contains("res.Undetermined = true;   // R48 M（2026-09-18，Fable 5.1）：判不了置它自己的位，不冒充「旋钮到顶」", s);
        Assert.Contains("res.StopWhy = $\"场解解不出来 ⇒ 判不了（{res.NullWhy}）\";\n                    res.Undetermined = true;", s.Replace("\r\n", "\n"));
        // 终局复核：判不了的整跑不许报可行
        Assert.Contains("res.Feasible = res.Best.AllOk && !res.Undetermined;", s);
    }

    [Fact]
    public void 门_判词只有一份_判不了不说再算一次同一句话_旋钮到顶才说()
    {
        var undet = new SolverResult { Undetermined = true, UndeterminedWhy = "片3 舌保温 13.036 处外层耦合未收敛" };
        string v = Solver.VerdictOf(undet);
        Assert.Contains("判不了", v);
        Assert.Contains("13.036", v);
        Assert.Contains("可加轮数上限／细化网格重算", v);
        Assert.DoesNotContain("再算一次会得到同一句话", v);
        Assert.DoesNotContain("**不可行**", v);        // 判词不许以「不可行」开头（句里「既不是可行也不是不可行」是解释，不是判词）
        Assert.StartsWith("**判不了**", v);

        var hit = new SolverResult { HitBound = true, StopWhy = "片0 舌保温 已在上界" };
        string h = Solver.VerdictOf(hit);
        Assert.Contains("再算一次会得到同一句话", h);
        Assert.Contains("不可行", h);

        Assert.Contains("可行", Solver.VerdictOf(new SolverResult { Feasible = true }));
        Assert.Contains("没搜到", Solver.VerdictOf(new SolverResult()));

        // 三处消费者都读它：命令行、界面、慢门的输出文件（同一句话不留五份）
        Assert.Contains("Solver.VerdictOf(rr)", Code("Pt_Optimize/Program.cs"));
        Assert.Contains("Solver.VerdictOf(srD8)", Code("Pt_Optimize/UI/LineDesignPage.cs"));
        Assert.Contains("Solver.VerdictOf(sr)", Code("Pt_Optimize.Tests/R48LEndToEndTests.cs"));
        // 界面／报告不许出现命令行开关名与判据代号（那两道门另管；这里只盯这句话本身）
        Assert.DoesNotContain("--", v);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ⑥ 界面：判不了不推去搜形状
    // ────────────────────────────────────────────────────────────────────

    private static FlowState Stuck() => new()
    {
        Last = new LineResult
        {
            Ok = true, Converged = true,
            Checks = new[]
            {
                new ConstraintOut { Name = LineResult.Key.ColdUnderTc, Kind = CheckKind.HardSafety, Unit = "K", Actual = 5.3, Limit = 5.0, Ok = false },
                new ConstraintOut { Name = LineResult.Key.FreeTab, Kind = CheckKind.HardSafety, Actual = 114.8, Limit = 100.0, Ok = true },
                new ConstraintOut { Name = LineResult.Key.DiscCover, Kind = CheckKind.HardSafety, Actual = 29.27, Limit = 0.0, Ok = true },
            },
        },
        CurrentSnap = "同一个快照", SolvedSnap = "同一个快照",
    };

    private static bool Analytic(string cmd) =>
        cmd is "shape.search" or "core.autoThick" or "core.runLine" or "core.verifyMesh" or "core.fineResolve";

    [Fact]
    public void 门_判不了不推去搜形状_指路说这一点没解到收敛()
    {
        var st = Stuck();
        Assert.Equal("core.autoThick", Flow.Next(st, Analytic)!.CmdId);   // 自证：没判不了时指的是自动定厚

        st.SolverUndetermined = true;
        st.SolverUndeterminedWhy = "片3 舌保温 13.036 处外层耦合未收敛";
        var ns = Flow.Next(st, Analytic);
        Assert.NotNull(ns);
        Assert.NotEqual("shape.search", ns!.CmdId);
        Assert.Equal("core.runLine", ns.CmdId);
        Assert.Contains("没解到收敛", ns.Why);
        Assert.Contains("可加轮数上限／细化网格重算", ns.Why);
        Assert.Contains("13.036", ns.Why);
        Assert.DoesNotContain("点「◇ 搜形状」", ns.Why);
        Assert.DoesNotContain("走到头", ns.Why);

        // 两位同时亮（不该发生，求解器互斥）时判不了优先 —— 不许「到顶」那句抢先把人推去搜形状
        st.SizerProvedInfeasible = true;
        Assert.Equal("core.runLine", Flow.Next(st, Analytic)!.CmdId);
    }

    [Fact]
    public void 门_判不了这一位在PushFlow里发布_参数一动就清_与到顶互斥()
    {
        string src = Code("Pt_Optimize/UI/LineDesignPage.cs");
        Assert.Contains("_solverUndetermined = srD8.Undetermined;", src);
        Assert.Contains("if (_solverUndetermined) _sizerInfeasible = false;", src);
        int a = src.IndexOf("private void PushFlow()", StringComparison.Ordinal);
        Assert.True(a > 0, "找不到 PushFlow —— 断言失去了对象");
        int b = src.IndexOf("\n    }", a, StringComparison.Ordinal);
        Assert.Contains("f.SolverUndetermined = _solverUndetermined;", src[a..b]);
        Assert.True(src.Split("_solverUndetermined = false;").Length - 1 >= 2, "参数一动就清：两条入口（参数表／页面控件）都要清");
        // 搜形状两张表与两族并列表：判不了要带停因、不许印成「不可行」
        Assert.Contains("(sr.HitBound || sr.Undetermined ? $\"（⚠ {sr.StopWhy}）\" : \"\")", src);
        Assert.Contains("s.Undetermined ? \"判不了\" : \"不可行\"", src);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ⑦ 二分在格点上走；格点判决走格
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void 门_二分在图纸格点上走_括号宽不超一格即停_源码钉死()
    {
        string s = Code("Pt_Optimize/Core/Solver.cs");
        Assert.Contains("double mid = SnapUpToGridMm(NextBisectPoint(knob, lo, hi), q);", s);
        Assert.Contains("for (int i = 0; i < opt.BisectMaxIter && hi - lo > q + 1e-9; i++)", s);
        Assert.Contains("var walk = WalkConservative(EvalAt, hiPt, at0, q, hiBound, opt.CertWalkMaxSteps, nm, Criteria.Plain(key), Log);", s);
        Assert.DoesNotContain("hi - lo > TolOf(opt, knob)", s);   // 旧写法（先收到 0.005 mm 再对齐）不许回来
        // 走格上限是成本闸，默认 10 格
        Assert.Equal(10, new SolverOptions().CertWalkMaxSteps);
    }

    [Fact]
    public void 门_下一格_在格上加一格_不在格上向上对齐()
    {
        Assert.Equal(0.5, Solver.NextGridUp(0.30, 0.5), 9);
        Assert.Equal(13.5, Solver.NextGridUp(13.0, 0.5), 9);
        Assert.Equal(0.74, Solver.NextGridUp(0.73, 0.01), 9);
        Assert.Equal(1.01, Solver.NextGridUp(1.00, 0.01), 9);
        Assert.Equal(7.0, Solver.NextGridUp(7.0, 0), 9);   // 不量化 ⇒ 原值
    }

    /// <summary>走格的纯函数：用人造的裕度函数当场验四种收场（判得了／走满／到上界／解不出来）。</summary>
    [Fact]
    public void 门_格点走格_裕度小于认证误差就往保守方向走一格_走到头判不了()
    {
        var log = new List<string>();
        // 舌保温：每 mm 改裕度 4 K（r48_U 实测 13.00→13.07 管根裕度 −0.25→+0.07 的量级），认证误差 0.05 K
        (double, double)? Eval(double v) => ((v - 13.0) * 4.0, 0.05);
        var ok = Solver.WalkConservative(Eval, 13.0, (0.0, 0.05), 0.5, 20.0, 10, "片3 舌保温", "管根低于热偶读数", log.Add);
        Assert.True(ok.Ok); Assert.False(ok.Undetermined);
        Assert.Equal(13.5, ok.Value, 9);                     // 13.0 处裕度 0 < 0.05 ⇒ 走一格到 13.5（裕度 2.0）
        Assert.Contains(log, l => l.Contains(BranchMarks.GridWalkConservative));

        // 认证误差 0（瓦的判据）⇒ 裕度 ≥ 0 就停，一步不走（行为与改前相同）
        var ok0 = Solver.WalkConservative(Eval, 13.0, (0.0, 0.0), 0.5, 20.0, 10, "片3 舌保温", "管孔净流入", log.Add);
        Assert.True(ok0.Ok); Assert.Equal(13.0, ok0.Value, 9);

        // 不灵敏：裕度恒 0.01 < 0.05 ⇒ 走满 10 格仍判不了 ⇒ 判不了（不过也不不过）
        log.Clear();
        var flat = Solver.WalkConservative(v => (0.01, 0.05), 1.00, (0.01, 0.05), 0.01, 2.5, 10, "片1 环倍率", "最热铂高出热偶读数", log.Add);
        Assert.False(flat.Ok); Assert.True(flat.Undetermined);
        Assert.Contains("判不了", flat.Why); Assert.Contains("已走满 10 格", flat.Why);
        Assert.Contains(log, l => l.Contains(BranchMarks.UndeterminedGridAtHi));
        Assert.Equal(10, log.Count(l => l.Contains(BranchMarks.GridWalkConservative)));

        // 到上界：19.5 起、上界 20.0 ⇒ 走到 20.0 仍小于认证误差 ⇒ 下一格 20.5 > 上界 ⇒ 判不了
        var hi = Solver.WalkConservative(v => (0.02, 0.05), 19.5, (0.01, 0.05), 0.5, 20.0, 10, "片3 舌保温", "管根低于热偶读数", log.Add);
        Assert.False(hi.Ok); Assert.True(hi.Undetermined); Assert.Contains("已到上界 20.000", hi.Why);

        // 走到的那一格解不出来 ⇒ 判不了
        var nul = Solver.WalkConservative(v => null, 13.0, (0.0, 0.05), 0.5, 20.0, 10, "片3 舌保温", "管根低于热偶读数", log.Add);
        Assert.False(nul.Ok); Assert.True(nul.Undetermined); Assert.Contains("解不出来", nul.Why);
    }

    // ────────────────────────────────────────────────────────────────────
    //  段解地板
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 段解地板三项降了十倍（电流二分 0.05 → 0.005 A、Picard 0.01 → 0.001 K、Bvp1D 0.005 → 0.0005 K），生产段解真的读它们；旧地板留给注射。
    /// 实测依据（deliverable/R48_M_停机容差阶梯_两点_本次开跑于2026-09-18_214343.txt）：旧地板下真残差停在 0.0013～0.0029 K、步长 0.006～0.027 K 打转，
    /// 乘放大 35.9 ⇒ 0.05 K 的目标要靠「碰巧一起下探」才停得下来（13.036 要 91 轮、13.074 271 轮没停）。
    /// </summary>
    [Fact]
    public void 门_段解地板降了十倍_生产段解读它_注射旧地板当场红()
    {
        var p = new DesignInputs();
        Assert.Equal(DesignInputs.SegCurrentTolADefault, p.SegCurrentTolA, 12);
        Assert.Equal(DesignInputs.SegPicardTolKDefault, p.SegPicardTolK, 12);
        Assert.Equal(DesignInputs.SegBvpTolKDefault, p.SegBvpTolK, 12);
        Assert.True(p.SegCurrentTolA <= DesignInputs.SegCurrentTolALegacy / 10 + 1e-12, "电流二分地板没降到旧值的十分之一");
        Assert.True(p.SegPicardTolK <= DesignInputs.SegPicardTolKLegacy / 10 + 1e-12, "Picard 地板没降到旧值的十分之一");
        Assert.True(p.SegBvpTolK <= DesignInputs.SegBvpTolKLegacy / 10 + 1e-12, "Bvp1D 地板没降到旧值的十分之一");
        // 生产段解读的是这三项（不许再写死）
        string s = Code("Pt_Optimize/Core/SegmentSolver.cs");
        Assert.Contains("xTol: p.SegCurrentTolA", s);
        Assert.Contains("Tol = p.SegBvpTolK", s);
        Assert.Contains("if (err < p.SegPicardTolK) break;", s);
        Assert.DoesNotContain("xTol: 0.05", s);
        Assert.DoesNotContain("Tol = 0.005 }", s);
        Assert.DoesNotContain("if (err < 0.01) break;", s);
        // 参数表复制（Clone 走 JSON）要带着它们，否则注射与生产会各走一套
        var q = SegmentSolver.Clone(new DesignInputs { SegCurrentTolA = DesignInputs.SegCurrentTolALegacy });
        Assert.Equal(DesignInputs.SegCurrentTolALegacy, q.SegCurrentTolA, 12);
        // 行为：同一算例，旧地板与新地板解出来的电流不许逐位相同（否则地板根本没接进段解）
        var rNew = SegmentSolver.Solve(new DesignInputs());
        var legacy = new DesignInputs { SegCurrentTolA = DesignInputs.SegCurrentTolALegacy, SegPicardTolK = DesignInputs.SegPicardTolKLegacy, SegBvpTolK = DesignInputs.SegBvpTolKLegacy };
        var rOld = SegmentSolver.Solve(legacy);
        Assert.True(rNew.Ok && rOld.Ok);
        Assert.NotEqual(rOld.CurrentA, rNew.CurrentA);
        Assert.True(Math.Abs(rOld.CurrentA - rNew.CurrentA) < 0.5, $"新旧地板电流差 {Math.Abs(rOld.CurrentA - rNew.CurrentA):0.000} A —— 地板改的是分辨率，差不该超过旧格子 0.05 A 的量级太多");
    }

    // ────────────────────────────────────────────────────────────────────
    //  证据头
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void 门_证据头写认证误差口径_容差口径说绝对目标()
    {
        var lc = new LineCase();
        Assert.Contains("绝对目标", EvidenceHeader.CoupleTolNote(lc));
        Assert.DoesNotContain("注射", EvidenceHeader.CoupleTolNote(lc));
        Assert.Contains("注射口径", EvidenceHeader.CoupleTolNote(new LineCase { CoupleTolFromMargin = true }));
        string cert = EvidenceHeader.CertErrNote(lc);
        Assert.Contains("认证误差 = 放大 × 停机残差", cert);
        Assert.Contains("max(闭式 1+ℓt/Δx × 1.1, 实测雅可比", cert);
        string head = EvidenceHeader.ForLineCase("门", lc);
        Assert.Contains("# 认证误差口径：", head);
        Assert.Contains("# 耦合容差口径：绝对目标", head);
    }
}
