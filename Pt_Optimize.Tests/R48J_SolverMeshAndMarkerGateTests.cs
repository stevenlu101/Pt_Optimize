using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★ R48 J 路（求解器与界面安全线，2026-09-15 Opus 5）的门。每条先有实验（deliverable/J路_*_本次开跑于*.txt，探针 R48J_ExperimentProbeTests），
/// 再修，再用这里的门钉住；每道门的「改回旧写法会红」注入实验记在 HANDOVER 的 J 路注记里。门只调生产函数，不手抄生产配方。
/// </summary>
public class R48J_SolverMeshAndMarkerGateTests
{
    private static string Src(params string[] rel) => File.ReadAllText(Path.Combine(HandoverDoc.Root(), Path.Combine(rel)));

    /// <summary>去掉整行注释（`//` 起头的行）—— 源码门查的是代码，不许被「原为 …」那种解释性注释本身命中（2026-09-16 Opus 5：J1、J11 两道源码门首跑就是这样红的）。</summary>
    private static string CodeOnly(string s)
        => string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string MeshFields(LineCase lc)
        => string.Join("　", typeof(LineCase).GetFields(BindingFlags.Public | BindingFlags.Instance)
                                                .Where(f => f.Name.StartsWith("Mesh", StringComparison.Ordinal))
                                                .Select(f => $"{f.Name}={f.GetValue(lc)}"));

    // ════════════════ J1（P1-2）：终局复核的网格与最后一遍求根的网格逐项相同
    /// <summary>
    /// 实验：deliverable/J路_J1_终局复核网格_本次开跑于2026-09-15_190654.txt —— W08、导航遍即终局（MaxRounds = 0）：求根算例细区半径 59、内带 2 mm，
    /// 终局算例半径 50、内带 0；片0 格数 1096 对 1006，Solve 的 res.Best.MeshCells = 1006（终局那次整线解就跑在没求过根的网格上）。
    /// 门：四种最后一遍选项（导航档、整张加密、粗筛、加密不给半径）下，Solver.FinishCase 造出的算例与「BuildCase + Solver.ApplyCaseMesh」的
    /// LineCase 全部 Mesh* 字段逐项相同；导航档那一格还要求半径真的不是 LineCase 缺省 50（否则门空转）。
    /// </summary>
    [Fact]
    public void J1_终局复核网格与最后一遍求根逐项相同()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone().Fit();
        var (fine, radius) = MeshVerify.RequiredMeshFor(d);
        Assert.NotEqual(new LineCase().MeshFineRadiusMm, radius);   // 非空转：W08 的细区半径不是缺省 50
        var opts = new[]
        {
            ("导航档（第一遍没走通即终局）", Solver.NavOptionsOf(new SolverOptions { FineMm = fine, FineRadiusMm = radius })),
            ("细网格第二遍", new SolverOptions { FineMm = fine, FineRadiusMm = radius }),
            ("粗筛", new SolverOptions { ScreenCoarseMm = 20.0 }),
            ("加密不给半径", new SolverOptions { FineMm = 0.5 }),
        };
        foreach (var (tag, o) in opts)
        {
            var root = d.BuildCase(p, checkRamp: false);
            Solver.ApplyCaseMesh(root, o);
            var fin = Solver.FinishCase(d, p, o);
            Assert.True(MeshFields(root) == MeshFields(fin), $"{tag}：求根 {MeshFields(root)}　终局 {MeshFields(fin)}");
            Assert.True(fin.CheckRamp, $"{tag}：终局复核要带升温");
        }
        var nav = Solver.FinishCase(d, p, opts[0].Item2);
        Assert.Equal(radius, nav.MeshFineRadiusMm);
        // 源码门（辅助）：Finish 造算例只走 FinishCase，FinishCase 只走 ApplyCaseMesh（旧的「if (lastOpt.FineMm > 0)」那一支不许回来）
        string s = Src("Pt_Optimize", "Core", "Solver.cs");
        Assert.Contains("var lcF = FinishCase(d, baseIn, lastOpt);", s);
        int at = s.IndexOf("public static LineCase FinishCase(", StringComparison.Ordinal);
        string body = CodeOnly(s[at..s.IndexOf("return lcF;", at, StringComparison.Ordinal)]);
        Assert.Contains("ApplyCaseMesh(lcF, lastOpt);", body);
        Assert.DoesNotContain("if (lastOpt.FineMm > 0)", body);
        Assert.DoesNotContain("RefineWholeMesh", body);
    }

    // ════════════════ J3（P1-4）：「判不了」后置标记 —— 判据表、求解器 Gate、逐片裕度读同一份
    private static LineResult Synthetic()
    {
        var r = new LineResult
        {
            Ok = true, Converged = true,
            Segments = new[] { new SegmentOut { Name = "段0", TRootAC = 1150, TRootBC = 1150, SetpointC = 1150 } },
            Flanges = new[] { new FlangeOut { Name = "入口", QFromTubeW = 3.0 }, new FlangeOut { Name = "出口", QFromTubeW = 4.0 } },
        };
        r.Checks = LineRunner.DependsOnFlangeFields
            .Concat(new[] { LineResult.Key.TubeJ, LineResult.Key.SectionJ, LineResult.Key.FreeTab })
            .Select(k => new ConstraintOut { Name = k, Kind = CheckKind.HardSafety, Ok = true, Actual = 1, Limit = 2 }).ToArray();
        return r;
    }

    private static void RunAllMarks(LineResult r)
    {
        var marks = typeof(LineRunner).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("MarkUndeterminedIf", StringComparison.Ordinal)).ToArray();
        Assert.True(marks.Length >= 5, $"LineRunner 的 MarkUndeterminedIf* 只找到 {marks.Length} 个 —— 门认不得它们了");
        foreach (var m in marks)
        {
            var ps = m.GetParameters();
            object?[] args = ps.Select(pi => pi.ParameterType == typeof(LineResult) ? r
                                          : pi.ParameterType == typeof(FlangeOut[]) ? r.Flanges
                                          : pi.ParameterType == typeof(SegmentOut[]) ? r.Segments
                                          : pi.ParameterType == typeof(bool) ? (object)false
                                          : throw new InvalidOperationException($"{m.Name} 的参数 {pi.Name}（{pi.ParameterType.Name}）门不认得 —— 补上")).ToArray();
            m.Invoke(null, args);
        }
    }

    /// <summary>
    /// 实验：deliverable/J路_J3_一片温度场不收敛_本次开跑于2026-09-15_192029.txt —— W08 导航网格、片 HC1|HC2 温度场压到 2 轮没收敛：
    /// 判据表管孔净流入／热侧／冷侧判不了，而 Solver.PlateSlack 照样给出 -9.070／-36.480／+26.671；Solve 第 1 轮拿「片1 裕度 -102.105」去挑旋钮、抬舌保温。
    /// 门（反射枚举 FlangeOut 与 SegmentOut 的全部布尔字段，逐个翻转）：翻转后五道后置标记里有任一道把判据判不了
    /// ⇔ LineRunner.PlateUndeterminedWhy 非空 ⇔ Solver.GateWhy 非空；标记在片上时该片三条逐片裕度都是 NaN、另一片不受影响。
    /// 新加一道标记而没接进唯一定义，这里当场红；把唯一定义里任何一道删掉，这里也红。
    /// </summary>
    [Fact]
    public void J3_判不了标记_判据表求解器逐片裕度同一份()
    {
        int hitsFlange = 0, hitsSeg = 0;
        foreach (var fld in typeof(FlangeOut).GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.FieldType == typeof(bool)))
        {
            var r = Synthetic();
            fld.SetValue(r.Flanges[1], !(bool)fld.GetValue(r.Flanges[1])!);
            RunAllMarks(r);
            bool table = r.Checks.Any(c => c.Undetermined);
            string why = LineRunner.PlateUndeterminedWhy(r, -1), gate = Solver.GateWhy(r);
            Assert.True(table == (why.Length > 0) && table == (gate.Length > 0),
                $"FlangeOut.{fld.Name} 翻转：判据表判不了 {table}，唯一定义「{why}」，求解器 Gate「{gate}」—— 三处不是同一份");
            if (!table) continue;
            hitsFlange++;
            foreach (var key in new[] { LineResult.Key.NetFlux, LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc })
                Assert.True(double.IsNaN(Solver.PlateSlack(r, key, 1, 5, 5)), $"FlangeOut.{fld.Name}：标记在片1，片1 的「{key}」逐片裕度应判不了");
            Assert.Equal(3.0, Solver.PlateSlack(r, LineResult.Key.NetFlux, 0, 5, 5));
            Assert.Equal("", LineRunner.PlateUndeterminedWhy(r, 0));
        }
        foreach (var fld in typeof(SegmentOut).GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.FieldType == typeof(bool)))
        {
            var r = Synthetic();
            fld.SetValue(r.Segments[0], !(bool)fld.GetValue(r.Segments[0])!);
            RunAllMarks(r);
            bool table = r.Checks.Any(c => c.Undetermined);
            string why = LineRunner.PlateUndeterminedWhy(r, -1), gate = Solver.GateWhy(r);
            Assert.True(table == (why.Length > 0) && table == (gate.Length > 0),
                $"SegmentOut.{fld.Name} 翻转：判据表判不了 {table}，唯一定义「{why}」，求解器 Gate「{gate}」—— 三处不是同一份");
            if (!table) continue;
            hitsSeg++;
            for (int j = 0; j < 2; j++)
                Assert.True(double.IsNaN(Solver.PlateSlack(r, LineResult.Key.NetFlux, j, 5, 5)), $"SegmentOut.{fld.Name}：管温场判不了，片{j} 的逐片裕度应判不了");
        }
        // 非空转：四道逐片标记 + 一道管散热表超界都要真的被翻到
        Assert.True(hitsFlange >= 4, $"逐片标记只触发了 {hitsFlange} 道（应 ≥ 4：场没解到位、保温分界判不了、压接盖孔、压接伸进圆盘）");
        Assert.True(hitsSeg >= 1, "管散热表超界那一道没触发");
        var clean = Synthetic(); RunAllMarks(clean);
        Assert.Equal("", Solver.GateWhy(clean));
    }

    /// <summary>源码门（辅助）：Gate 在 Ok、Converged 之后读 GateWhy 并回 null；「没有旋钮能治」之前先挑判不了；安装报告逐片表读同一份定义。</summary>
    [Fact]
    public void J3_Gate与停机病因与安装报告接线()
    {
        string s = Src("Pt_Optimize", "Core", "Solver.cs");
        int g = s.IndexOf("private static LineResult? Gate(LineResult r, SolverResult res)", StringComparison.Ordinal);
        string gate = s[g..s.IndexOf("public static string GateWhy(", g, StringComparison.Ordinal)];
        Assert.True(gate.IndexOf("string und = GateWhy(r);", StringComparison.Ordinal) > gate.IndexOf("if (!r.Converged)", StringComparison.Ordinal));
        Assert.Contains("res.NullWhy = \"判不了 —— \" + und;", gate);
        int u = s.IndexOf("var undetNow = Violations(last).Where(c => c.Undetermined).ToList();", StringComparison.Ordinal);
        int rest = s.IndexOf("var rest = Violations(last)", StringComparison.Ordinal);
        Assert.True(u > 0 && u < rest, "「没有旋钮能治」之前要先把判不了的判据挑出来单说");
        Assert.Contains("LineRunner.PlateUndeterminedWhy(r, t.Plate)", Src("Pt_Optimize", "Core", "InstallReport.cs"));
    }

    // ════════════════ J4（P1-5）：加密复算 —— 没收敛或判不了的档不许进比较、不许打已验戳
    /// <summary>
    /// 实验：deliverable/J路_J4_加密复算耦合没收敛_本次开跑于2026-09-15_190656.txt —— W08、外层耦合压到 3 轮（剩余误差估计 578～5262 K），四档全部照常进档间比较，
    /// 判词写成「这几条在这个网格族上判不了……序列在摆，不是还没收敛」—— 把耦合没收敛说成网格族的事。
    /// 门：MeshVerify.TierUnusableWhy 对没收敛、带判不了标记、复核三条判不了三种合成结果都非空、对干净结果为空；
    /// 主循环在解出这一档之后、记进 Trace／Line 之前先问它；界面已验戳与换判据表读同一个条件。
    /// </summary>
    [Fact]
    public void J4_加密复算的档可引用条件()
    {
        LineResult Clean()
        {
            var r = Synthetic();
            r.Checks = new[] { LineResult.Key.NetFlux, LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc }
                .Select(k => new ConstraintOut { Name = k, Kind = CheckKind.HardSafety, Ok = true, Actual = 1, Limit = 5 }).ToArray();
            return r;
        }
        Assert.Equal("", MeshVerify.TierUnusableWhy(Clean()));
        var a = Clean(); a.Converged = false;
        Assert.Contains("外层耦合没收敛", MeshVerify.TierUnusableWhy(a));
        var b = Clean(); b.Flanges[0].FieldsConverged = false; b.Flanges[0].FieldNote = "温度场未收敛（合成）";
        Assert.Contains("场没解到位", MeshVerify.TierUnusableWhy(b));
        var c = Clean(); c.Checks[2].Undetermined = true;
        Assert.Contains("判不了", MeshVerify.TierUnusableWhy(c));
        var e = Clean(); e.Checks[1].Actual = double.NaN;
        Assert.Contains("判不了", MeshVerify.TierUnusableWhy(e));

        string s = Src("Pt_Optimize", "Core", "MeshVerify.cs");
        int run = s.IndexOf("var r = LineRunner.Run(lc, inner, cancel);", StringComparison.Ordinal);
        int ask = s.IndexOf("string unusable = TierUnusableWhy(r);", run, StringComparison.Ordinal);
        int line = s.IndexOf("res.Line = r;", run, StringComparison.Ordinal);
        int trace = s.IndexOf("res.Trace.Add((h,", run, StringComparison.Ordinal);
        Assert.True(run > 0 && ask > run && ask < line && ask < trace, "主循环要在记进 Line／Trace 之前先问这一档能不能用");
        string page = Src("Pt_Optimize", "UI", "LineDesignPage.cs");
        Assert.Contains("bool verifiedUsable = res.Converged && res.Line is { Ok: true } && MeshVerify.TierUnusableWhy(res.Line).Length == 0;", page);
        Assert.Contains("_verifiedSnap = verifiedUsable ? snapAtStart : null;", page);
        Assert.Contains("if (verifiedUsable)", page);
    }

    /// <summary>行为门：一档外层耦合没收敛（W08 一段、耦合轮数压到 1），判词如实说是耦合没收敛，不收敛、不记档、不给 Line。</summary>
    [Fact]
    public void J4_没收敛的档判词如实()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        var fac = MeshVerify.AnalyticCaseFactory(d, p, radius, radius);
        var res = MeshVerify.Run((a, b) => { var lc = fac(a, b); lc.CoupleMaxRounds = 1; lc.CheckRamp = false; return lc; },
                                 2.0, radius, radius, maxCells: 40000, maxRounds: 3);
        Assert.False(res.Converged);
        Assert.Contains("外层耦合没收敛", res.Verdict);
        Assert.Contains("不能说网格无关", res.Verdict);
        Assert.Empty(res.Trace);
        Assert.Null(res.Line);
    }

    // ════════════════ J5（P1-6）：搜形状精算没全过 ⇒ 不叫「全过形状」、不写回
    [Fact]
    public void J5_搜形状精算结论只看Feasible()
    {
        var no = ShapeSearchPlan.RefineVerdict(false, "细网格第二遍跑满 40 轮仍未全过 —— **未收敛**，不作数", "第 1 族　");
        Assert.False(no.WriteBack);
        Assert.DoesNotContain("最轻的全过形状", no.Headline);
        Assert.Contains("没有全过", no.Headline);
        Assert.Contains("未收敛", no.Headline);
        Assert.DoesNotContain("**未收敛**", no.Headline);
        var yes = ShapeSearchPlan.RefineVerdict(true, "", "");
        Assert.True(yes.WriteBack);
        Assert.Contains("最轻的全过形状", yes.Headline);
        // 源码门：页面在印「最轻的全过形状」与写回之前先问 RefineVerdict，不写回就 return null
        string page = Src("Pt_Optimize", "UI", "LineDesignPage.cs");
        int ask = page.IndexOf("var refine = ShapeSearchPlan.RefineVerdict(fin.Feasible, fin.StopWhy, famPrefix);", StringComparison.Ordinal);
        Assert.True(ask > 0);
        string after = page[ask..(ask + 400)];
        Assert.Contains("if (!refine.WriteBack)", after);
        Assert.Contains("return null;", after);
        Assert.DoesNotContain("$\"\\r\\n★ {famPrefix}最轻的全过形状\\r\\n\"", page);
    }

    // ════════════════ J6（P1-7）：整片热稳定逐片评、取裕度最小；发热 NaN ⇒ 判不了
    private static readonly Lazy<(DesignSpec D, LineCase Lc, LineResult R)> OneSeg = new(() =>
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        var lc = d.BuildCase(p, checkRamp: false);
        var r = LineRunner.Run(lc);
        Assert.True(r.Ok, r.Message);
        return (d, lc, r);
    });

    /// <summary>
    /// 实验：deliverable/J路_J6_整片热稳定逐片评_0_本次开跑于2026-09-15_191234.txt —— W08 四片：发热最大的入口片 964.85 W 裕度 10.5231，出口片 796.07 W 裕度 9.6754 更小，
    /// 生产 FlangeLumped 选的是入口片。一段两片的算例上两者恰好是同一片（J路_J6乙_…_194151.txt），所以门在同一个整线解上把片0 的圆盘保温加厚（只改集总模型读的算例量，不重解场），
    /// 造出「发热小的片裕度小」：FlangeLumped 必须选裕度最小的那片、且确认它不是发热最大的那片（非空转）；再把一片发热置 NaN，整条必须判不了。
    /// </summary>
    [Fact]
    public void J6_整片热稳定逐片评取裕度最小_NaN判不了()
    {
        var (d0, lc0, r) = OneSeg.Value;
        int small = r.Flanges[0].QGenW < r.Flanges[1].QGenW ? 0 : 1, big = 1 - small;
        var d2 = d0.Clone();
        d2.TabInsulMm = (double[])d0.TabInsulMm.Clone();
        d2.TabInsulMm[small] = 60.0;                                // 发热小的那片舌保温加厚 ⇒ 集总模型里它的散热导数变小、裕度变小（场不重解）
        var lc = d2.BuildCase(new DesignInputs(), checkRamp: false);
        var per = Enumerable.Range(0, r.Flanges.Length).Select(j => LineRunner.FlangeLumpedAt(lc, r.Flanges, j)!).ToArray();
        int argmin = per[0].Stab.Margin <= per[1].Stab.Margin ? 0 : 1;
        Assert.True(argmin != big, $"门空转：发热最大的片 {big} 恰好也是裕度最小的片（裕度 {per[0].Stab.Margin:0.000}／{per[1].Stab.Margin:0.000}）—— 换一个加厚量");
        var pick = LineRunner.FlangeLumped(lc, r.Flanges)!;
        Assert.Equal(argmin, pick.Index);
        Assert.Equal(per[argmin].Stab.Margin, pick.Stab.Margin);
        Assert.Contains("取裕度最小", pick.SelectNote);

        double keep = r.Flanges[small].QGenW;
        try
        {
            r.Flanges[small].QGenW = double.NaN;
            var blind = LineRunner.FlangeLumped(lc0, r.Flanges)!;
            Assert.True(blind.Stab.Undetermined, "一片发热算不出，整片热稳定应判不了");
            Assert.Equal(small, blind.Index);
            Assert.Contains(small, blind.StabUndeterminedPlates);
            Assert.Contains("整条判不了", LineRunner.FlangeStabNote(blind));
        }
        finally { r.Flanges[small].QGenW = keep; }
    }

    /// <summary>
    /// P2-9 与 P1-4 在报告上的接线（行为门，同一个整线解）：配方偏离非空时安装报告紧跟判定写明「不是生产口径」；某片带「判不了」标记时逐片表那一行印判不了。
    /// </summary>
    [Fact]
    public void J3与P2_9_安装报告读判不了标记与配方偏离()
    {
        var (d, _, r) = OneSeg.Value;
        var p = new DesignInputs();
        string clean = InstallReport.Build(r, d, p);
        Assert.DoesNotContain("配方与生产不同", clean);
        var keepRecipe = r.Flanges[0].MeshRecipe;
        try
        {
            r.Flanges[0].MeshRecipe = null;
            string dev = InstallReport.Build(r, d, p);
            Assert.Contains("与生产不同 —— 本报告的数不是生产口径", dev);
            Assert.Contains(r.Flanges[0].Name + "：网格没有配方记录", dev);
        }
        finally { r.Flanges[0].MeshRecipe = keepRecipe; }
        bool keepConv = r.Flanges[1].FieldsConverged; string keepNote = r.Flanges[1].FieldNote;
        try
        {
            r.Flanges[1].FieldsConverged = false; r.Flanges[1].FieldNote = "温度场未收敛（门里合成）";
            string rep = InstallReport.Build(r, d, p);
            // 报告里逐片的表不止一张（铜排、保温、焊脚…都以片名起头）：只在「热偶读数基准」那张表里找本片那一行
            var lines = rep.Replace("\r\n", "\n").Split('\n');
            int head = Array.FindIndex(lines, l => l.StartsWith("片\t热偶读数基准", StringComparison.Ordinal));
            Assert.True(head >= 0, "安装报告里没有热偶读数基准那张表");
            string row = lines.Skip(head + 1).First(l => l.StartsWith(r.Flanges[1].Name + "\t", StringComparison.Ordinal));
            Assert.Contains("判不了", row);
            Assert.Contains("场没解到位", row);
            string row0 = lines.Skip(head + 1).First(l => l.StartsWith(r.Flanges[0].Name + "\t", StringComparison.Ordinal));
            Assert.DoesNotContain("判不了", row0);   // 标记在片1，片0 那一行照常给数
        }
        finally { r.Flanges[1].FieldsConverged = keepConv; r.Flanges[1].FieldNote = keepNote; }
    }

    // ════════════════ J7（P1-8）：定尺寸器的三个判定式遇 NaN ⇒ 判不了
    /// <summary>实验：deliverable/J路_J7_定尺寸器判定式遇NaN_本次开跑于2026-09-15_190531.txt（改前三个式子 NaN 一律被跳过：worst = 0、LevelHottestC = −∞／1300）。</summary>
    [Fact]
    public void J7_定尺寸器判定式NaN即判不了()
    {
        var v = new LineResult
        {
            Flanges = new[] { new FlangeOut { QFromTubeW = 10.0 }, new FlangeOut { QFromTubeW = double.NaN } },
            Segments = new[] { new SegmentOut { FlangeDipK = 1.0 }, new SegmentOut { FlangeDipK = double.NaN } },
        };
        Assert.True(double.IsNaN(FlangeAutoSizer.DrawOffTargetWorstW(v, 10.0)));
        Assert.True(double.IsNaN(FlangeAutoSizer.DipOffTargetWorstK(v, 10.0)));
        Assert.True(double.IsNaN(FlangeAutoSizer.LevelHottestC(new FlangeOut { LevelTMaxC = new[] { double.NaN, double.NaN } })));
        Assert.True(double.IsNaN(FlangeAutoSizer.LevelHottestC(new FlangeOut { LevelTMaxC = new[] { 1300.0, double.NaN } })));
        // 有限输入与改前逐位相同
        var ok = new LineResult
        {
            Flanges = new[] { new FlangeOut { QFromTubeW = 10.0 }, new FlangeOut { QFromTubeW = 9.5 } },
            Segments = new[] { new SegmentOut { FlangeDipK = 1.0 }, new SegmentOut { FlangeDipK = 12.5 } },
        };
        Assert.Equal(0.5, FlangeAutoSizer.DrawOffTargetWorstW(ok, 10.0), 12);
        Assert.Equal(2.5, FlangeAutoSizer.DipOffTargetWorstK(ok, 10.0), 12);
        Assert.Equal(1300.0, FlangeAutoSizer.LevelHottestC(new FlangeOut { LevelTMaxC = new[] { 1300.0, 1200.0 } }));
        Assert.Equal(double.NegativeInfinity, FlangeAutoSizer.LevelHottestC(new FlangeOut()));
        // 源码门：内层记 NaN 级并停、加厚试探**加到工艺上界那一次**遇 NaN 停、复核文字说判不了、收尾不算达标
        //   ⚠ 2026-09-17 Opus 5（J 路，独立复核）：原来这句写「加厚试探遇 NaN 停」——**二分那三步当时没停**（判不了当成「还过热」，出口照印「压住了」）。
        //   二分那一道现在由 J7乙 守（MinRaiseFactor），这里只管上界那一次；措辞照实改。
        string s = Src("Pt_Optimize", "Core", "FlangeAutoSizer.cs");
        Assert.Contains("if (double.IsNaN(tm)) undetLevels.Add(", s);
        Assert.Contains("if (double.IsNaN(hotHi))", s);
        Assert.True(s.IndexOf("if (double.IsNaN(hotHi))", StringComparison.Ordinal) < s.IndexOf("加厚这一级救不了它**（实测，不是估的）", StringComparison.Ordinal));
        Assert.Contains("if (levelUndetWhy.Length > 0)", s);
    }

    // ════════════════ P3-11：页面快照逐片记 —— 两片对调（平均值／最大值不变）也得算「改了」
    /// <summary>
    /// 2026-09-16 Opus 5：Snap 此前只记板厚平均、环倍率平均、槽张角最大、孔径最大；两片对调时快照不变 ⇒ 假新鲜、「这次改了什么」一行都不出。
    /// 门：六组逐片控件各自把片 0／片 1 设成（下限，上限）再对调，CurrentSnap 前后必须不等。去掉 CurrentSnap 里任一组 *ByPlate，对应那一组这里红。
    /// </summary>
    [Fact]
    public void P3_11_快照逐片记_两片对调也算改了()
    {
        var page = new PtOptimize.UI.LineDesignPage(new DesignInputs());
        var t = typeof(PtOptimize.UI.LineDesignPage);
        t.GetField("_suppressAuto", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, true);
        var snap = t.GetMethod("CurrentSnap", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var name in new[] { "_tPlate", "_tabIns", "_ringMul", "_slotDeg", "_holeR", "_holeAsp" })
        {
            var ctl = (System.Windows.Forms.NumericUpDown[])t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
            Assert.True(ctl.Length >= 2, $"{name} 不足两片，门空转");
            decimal lo = ctl[0].Minimum, hi = ctl[0].Maximum;
            Assert.True(lo < hi, $"{name} 上下限相同");
            ctl[0].Value = lo; ctl[1].Value = hi;
            var a = snap.Invoke(page, null)!;
            ctl[0].Value = hi; ctl[1].Value = lo;      // 对调：平均值、最大值都不变
            var b = snap.Invoke(page, null)!;
            Assert.False(a.Equals(b), $"{name}：两片对调后快照没变 —— 平均值／最大值相同就被当成「没改」");
        }
        // ★ 2026-09-17 Opus 5（J 路）：**新状态位要在下游造门** —— 上面只证明快照记住了逐片值；
        //   还要证明「这次改了什么」真的读它（否则就是又一个「赋了值没人读」），以及「第一次解就已经全过」那句不再恒印。
        string src = Src("Pt_Optimize", "UI", "LineDesignPage.cs");
        int mb = src.IndexOf("var movedByPlate = new (string Name, string A, string B, string U)[]", StringComparison.Ordinal);
        Assert.True(mb > 0, "「这次改了什么」里没有逐片那一组 —— 快照记了没人读");
        string block = src[mb..(mb + 900)];
        foreach (string fld in new[] { "PlateByPlate", "TabInsByPlate", "RingMulByPlate", "SlotByPlate", "HoleRByPlate", "HoleAspByPlate" })
            Assert.Contains("now." + fld, block);
        Assert.Contains("if (moved.Length == 0 && movedByPlate.Length == 0 &&", src);
        Assert.Contains("sb.AppendLine(r.AllOk ? \"◆ 这次没改动你填的任何一个数（第一次解就已经全过）。\"", src);
        Assert.Contains("foreach (var (nm, x, y, u) in movedByPlate)", src);
    }

    // ════════════════ J11（P2-14）：升温快筛的目标温度读 LineCase.RampTargetC，文字照实
    [Fact]
    public void J11_升温快筛目标温度只从LineCase来_文字照实()
    {
        var p = new DesignInputs();
        Assert.Equal(new LineCase().RampTargetC, RampScreen.TargetC);
        Assert.Equal(RampScreen.Evaluate(p, 20.0, 0.8, new LineCase().RampTargetC).Margin, RampScreen.Evaluate(p, 20.0, 0.8).Margin);
        var c = RampScreen.Judge(p, 20.0, 0.8);
        Assert.DoesNotContain("72 h", c.Note);
        Assert.Contains("不是同一个量", c.Note);
        var c900 = RampScreen.Judge(p, 20.0, 0.8, 900);
        Assert.Contains("900 °C", c900.Note);
        string s = CodeOnly(Src("Pt_Optimize", "Core", "RampScreen.cs"));
        Assert.DoesNotContain("const double TargetC", s);
        Assert.Contains("public static double TargetC => new LineCase().RampTargetC;", s);
        Assert.DoesNotContain("（限 72 h）", s);
        Assert.Contains("RampScreen.Judge(\n                _base, (double)_tubeIns.Value, (double)_wall.Value, rampTargetC);", Src("Pt_Optimize", "UI", "LineDesignPage.cs").Replace("\r\n", "\n"));
    }

    // ════════ J2 连带：定尺寸器底算例带解析板件时当场说
    /// <summary>
    /// 2026-09-16 Opus 5（J 路，J2 连带的行为改动，上一次没有门）：CloneCase 现在拷 <c>FlangePlates</c>（此前漏拷）——
    /// 于是底算例若带解析板件，定尺寸器的厚度标度（<see cref="FlangeAutoSizer.Solve"/> 的 t）与逐级标度（<see cref="FlangeAutoSizer.SolveByLevel"/> 的 LevelScale）
    /// 会被板件**静默盖掉**：改了等于没改，而搜索与全精度复核照样报一个数。此前漏拷时副本没有板件，走的是「未指定法兰几何」当场失败那条路；
    /// 拷上之后不当场说就成了看起来正常的错数 ⇒ 两个入口各加一道闸，当场抛 <see cref="ArgumentException"/>。
    /// 门：带板件的算例在两个入口都当场抛、消息点名「解析板件」；不带板件的算例**过得了这道闸**（用已取消的取消令牌证明它走到了闸后面的第一句 —— 非空转）。
    /// 把任一处 throw 去掉，这里红。
    /// </summary>
    [Fact]
    public void J2乙_定尺寸器底算例带解析板件时当场说()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone().Fit();
        var withPlates = d.BuildCase(p, checkRamp: false);
        Assert.NotEmpty(withPlates.FlangePlates);
        var ex1 = Assert.Throws<ArgumentException>(() => FlangeAutoSizer.Solve(withPlates, null, new[] { 1.0 }));
        Assert.Contains("解析板件", ex1.Message);
        var ex2 = Assert.Throws<ArgumentException>(() => FlangeAutoSizer.SolveByLevel(withPlates, new[] { new[] { 1.0 } }));
        Assert.Contains("解析板件", ex2.Message);
        // ★ 2026-09-17 Opus 5（J 路，独立复核「应修 4」：闸比文档严）：逐级那道**不问** makePlateByLevel —— 给了照样抛。
        //   交接文档与最终报告原来都写成「带解析板件**却没** makePlateByLevel 时才抛」，与代码不符（这里此前也只验了没给的那一半）。
        //   现在按代码的真行为钉住：严得有理由 —— 底算例指着 .3dm 时全精度复核那一段故意不覆写 FlangePlates，而 CloneCase 会把底算例的板件带进去盖掉图纸。
        var ex3 = Assert.Throws<ArgumentException>(() => FlangeAutoSizer.SolveByLevel(
            withPlates, new[] { new[] { 1.0 } }, makePlateByLevel: _ => withPlates.FlangePlates[0]));
        Assert.Contains("解析板件", ex3.Message);

        // 非空转：图纸路径的底算例（不带板件）不在这道闸上停 —— 已取消的令牌让它停在闸后面的第一句
        var noPlates = d.BuildCase(p, checkRamp: false);
        noPlates.FlangePlates = Array.Empty<FlangePlate>();
        var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FlangeAutoSizer.Solve(noPlates, null, new[] { 1.0 }, cancel: cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => FlangeAutoSizer.SolveByLevel(noPlates, new[] { new[] { 1.0 } }, cancel: cts.Token));
    }

    // ════════════════ J7 乙（2026-09-17 Opus 5，独立复核「必须修 1」）：加厚试探的**二分**遇算不出 ⇒ 判不了，不许印「压住了」
    /// <summary>
    /// 复核查出的病（改动前后都在）：J7 只在「加厚到工艺上界」那一次挡了 NaN（`if (double.IsNaN(hotHi))`），紧接着的二分没挡 ——
    /// <c>if (HotAt(mid) &lt;= 阈值) hi = mid; else lo = mid;</c>，而 <c>NaN &lt;= 阈值</c> 为**假** ⇒ 走 <c>lo = mid</c>，
    /// **把「判不了」当成「还过热」**，出口照印「✓ 第 N 级加厚 ×k 压住了（最小够用的那一档，再薄就过热）」—— 那一档一次都没量出来。
    /// 方向上偏厚（比改前安全、费铂），但仍是「判不了当结论」。
    ///
    /// 门（行为，调生产那一份 <see cref="FlangeAutoSizer.MinRaiseFactor"/> —— 生产的二分就是它，门不另写一份）：
    ///   ① **中间档算不出的算例**：第一个中间档回 NaN ⇒ 判不了、记下是哪个倍数、当场停（不再白花解）；
    ///   ② 全程有数：步数与步长门槛与改动前逐位相同（问 1.5／1.25／1.125，取上界 1.25）—— 非空转；
    ///   ③ 「还过热」（大于阈值）与「算不出」必须分得开：前者继续二分、不判不了。
    /// 源码门：SolveByLevel 拿到「判不了」就置 <c>levelUndetWhy</c> 并 <c>break</c>（与上界那道同句式），「压住了」那句在它之后、不在同一支；
    /// 收尾仍走 <c>if (levelUndetWhy.Length > 0)</c> ⇒ 判词写「判不了，停止迭代」。把 MinRaiseFactor 里的 NaN 闸删掉，这里红。
    /// </summary>
    [Fact]
    public void J7乙_加厚二分中间档算不出就判不了()
    {
        const double limit = 1300.0, hiK = 2.0;

        // ① 中间档算不出
        var asked = new List<double>();
        var undet = FlangeAutoSizer.MinRaiseFactor(k => { asked.Add(k); return double.NaN; }, hiK, limit);
        Assert.True(undet.Undetermined, "中间档算不出（NaN）却没判「判不了」—— 又把判不了当成「还过热」了");
        Assert.Equal(1.5, undet.UndetAtK, 12);
        Assert.Single(asked);                       // 判不了就停

        // ② 全程有数：与改动前逐位相同
        var seen = new List<double>();
        var ok = FlangeAutoSizer.MinRaiseFactor(k => { seen.Add(k); return k >= 1.2 ? 1200.0 : 1400.0; }, hiK, limit);
        Assert.False(ok.Undetermined);
        Assert.Equal(new[] { 1.5, 1.25, 1.125 }, seen);
        Assert.Equal(1.25, ok.K, 12);

        // ③ 「还过热」与「算不出」分得开
        var hotAll = FlangeAutoSizer.MinRaiseFactor(_ => 1400.0, hiK, limit);
        Assert.False(hotAll.Undetermined);
        Assert.Equal(hiK, hotAll.K, 12);

        // 源码门：生产的二分就是它；判不了那一支在「压住了」之前，且是 levelUndetWhy + break
        string s = CodeOnly(Src("Pt_Optimize", "Core", "FlangeAutoSizer.cs"));
        Assert.Contains("var rb = MinRaiseFactor(HotAt, hiK, opt.OverheatRaiseFromC);", s);
        Assert.DoesNotContain("if (HotAt(mid) <= opt.OverheatRaiseFromC) hi = mid; else lo = mid;", s);
        int undetAt = s.IndexOf("if (rb.Undetermined)", StringComparison.Ordinal);
        int holdAt = s.IndexOf("压住了（最小够用的那一档", StringComparison.Ordinal);
        Assert.True(undetAt > 0, "SolveByLevel 没有「二分判不了」那一支");
        Assert.True(holdAt > undetAt, "「压住了」那句必须在判不了那道闸之后");
        string blk = s[undetAt..holdAt];
        Assert.Contains("levelUndetWhy =", blk);
        Assert.Contains("break;", blk);
        Assert.Contains("✗ **判不了，停止迭代**", s);     // 收尾判词按判不了写
    }

    // ════════════════ J6 丙（2026-09-17 Opus 5，独立复核「必须修 2」）：升温两节点逐片结果**两支都印**，并点名是哪一片
    /// <summary>
    /// 复核查出：「升温两节点逐片结果进附注」只接在 <c>if (lumped.Ramp is not null)</c> 那一支（经 RampPendingNote），
    /// 而两份 J6 证据里四片全「升温两节点 算不出」⇒ 生产实测走的一直是 <c>else</c> 支，那里只印**选中那一片**的错误原因、
    /// 位置栏还是「—」，界面上连是哪一片都没说（而挑片口径 09-15 刚从「发热最大」改成「裕度最小」）。
    ///
    /// 门（行为）：造一个**四片都算不出**的结果 ⇒ 附注四片各一条、各自点名并带自己的原因（调生产那一份 <see cref="LineRunner.RampPerPlateNote"/>）；
    /// 解出来的那片印「两节点解出」而不是「算不出」（非空转）。再在真整线解上验一次：那一行的附注把每一片的名字都印出来了。
    /// 源码门：两支都接同一份 —— 解出来那支经 RampPendingNote（它自己调 RampPerPlateNote），算不出那支直接调，且点名选中片与挑片口径。
    /// </summary>
    [Fact]
    public void J6丙_升温逐片附注两支都印_点名到片()
    {
        var names = new[] { "入口", "HC1|HC2", "HC2|HC3", "出口" };
        var all = names.Select((nm, j) => new LineRunner.FlangeLumpedOut
        {
            Index = j, PlateName = nm, Ramp = null, RampError = $"法兰节点标定不了：第{j}片的原因",
        }).ToArray();
        foreach (var x in all) x.PerPlate = all;

        string note = LineRunner.RampPerPlateNote(all[3]);   // 选中片 = 裕度最小那片（这里取片3，与 09-17 W08 实跑一致）
        foreach (var x in all)
        {
            Assert.Contains(x.PlateName, note);
            Assert.Contains(x.RampError, note);
        }
        Assert.Equal(4, note.Split("算不出").Length - 1);

        // 非空转：解出来的那片印的不是「算不出」
        all[1].Ramp = new RampTwoNodeResult { MaxFlangeMinusTubeK = 12.3 };
        string mixed = LineRunner.RampPerPlateNote(all[3]);
        Assert.Equal(3, mixed.Split("算不出").Length - 1);
        Assert.Contains("HC1|HC2 两节点解出", mixed);

        // 真整线解上再验一次：判据表那一行的附注逐片点名（本算例走的就是「算不出」那一支）
        var (_, _, r) = OneSeg.Value;
        var row = r.Checks.First(x => x.Name.StartsWith(LineResult.Key.RampField, StringComparison.Ordinal));
        Assert.Contains("逐片：", row.Note);
        foreach (var f in r.Flanges) Assert.Contains(f.Name, row.Note);

        // 源码门：两支都接同一份；算不出那支点名选中片与挑片口径
        string s = CodeOnly(Src("Pt_Optimize", "Core", "LineRunner.cs"));
        Assert.Contains("+ detail + RampPerPlateNote(o);", s);
        int ifAt = s.IndexOf("if (lumped.Ramp is not null)", StringComparison.Ordinal);
        Assert.True(ifAt > 0, "Judge 里找不到升温那一行的两支");
        string two = s[ifAt..(ifAt + 2500)];
        Assert.Contains("Note = RampPendingNote(c, lumped)", two);
        Assert.Contains("+ RampPerPlateNote(lumped)", two);
        Assert.Contains("按整片热稳定裕度最小挑出来的那一片", two);
    }

    // ════════════════ P3-10（2026-09-17 Opus 5，独立复核「应修 3」）：管根算不出 ⇒ 不许判收敛（行为门，上一版只有一句注释）
    /// <summary>
    /// 复核查出：P3-10 改的是**判定口径**（Converged=false ⇒ 下游 TierUnusableWhy、求解器 Gate 全线判不了），却是全轮唯一一条「行为变了、没门也没注入」，
    /// 而交接文档 J12 行的「门」栏写着「其余为注记」。⇒ 把步长与收敛判定提成生产函数（<see cref="LineRunner.CoupleRootStep"/>／<see cref="LineRunner.CoupleConverged"/>），这里造合成解直接调它们。
    ///
    /// 门：造一段 <c>TRootAC = NaN</c> 的合成解 —— 步长照样很小（NaN 被跳过，**这正是病**）、但不许判收敛；
    /// 同一组数把 NaN 换成有限值就判收敛（非空转）；前两条（步长距离、真残差）也各验一次，删掉任一条这里红。
    /// </summary>
    [Fact]
    public void P3_10_管根算不出不许判收敛()
    {
        var prev = new[] { new SegmentOut { TRootAC = 1000, TRootBC = 1010 },
                           new SegmentOut { TRootAC = 1020, TRootBC = 1030 } };
        var next = new[] { new SegmentOut { TRootAC = 1000.2, TRootBC = 1010.1 },
                           new SegmentOut { TRootAC = double.NaN, TRootBC = 1030.05 } };
        var bad = LineRunner.CoupleRootStep(prev, next, 2);
        Assert.True(bad.RootNaN);
        Assert.Equal(0.2, bad.Delta, 9);        // NaN 被跳过 ⇒ 步长「很小」：不加第三条就会当成收敛
        Assert.False(LineRunner.CoupleConverged(bad.Delta * 25, 0.0, 10.0, bad.RootNaN),
                     "有段管根算不出还判了收敛 —— 判不了不算过");

        next[1].TRootAC = 1020.1;               // 非空转：同一组数没有 NaN 就判收敛
        var good = LineRunner.CoupleRootStep(prev, next, 2);
        Assert.False(good.RootNaN);
        Assert.Equal(0.2, good.Delta, 9);
        Assert.True(LineRunner.CoupleConverged(good.Delta * 25, 0.0, 10.0, good.RootNaN));

        // 三条各验一次
        Assert.False(LineRunner.CoupleConverged(50.0, 0.0, 10.0, false));   // 步长距离那条
        Assert.False(LineRunner.CoupleConverged(5.0, 1.0, 10.0, false));    // 真残差那条（1.0 × 25 > 10）
        Assert.False(LineRunner.CoupleConverged(5.0, 0.0, 10.0, true));     // 管根算不出那条

        // 源码门：耦合主循环调的就是这两份（不许有人在循环里另写一份判定）
        string s = CodeOnly(Src("Pt_Optimize", "Core", "LineRunner.cs"));
        Assert.Contains("var step = CoupleRootStep(res.Segments, next.Segments, c.SegmentCount);", s);
        Assert.Contains("if (CoupleConverged(remain, resK, c.CoupleTolK, rootNaN))", s);
        Assert.DoesNotContain("if (remain < c.CoupleTolK && resOk", s);
    }
}
