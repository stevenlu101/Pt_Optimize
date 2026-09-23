using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 B（2026-09-14 Opus 5）：**判据口径改为热偶读数基准** 的快门。
///
/// 用户 2026-09-14 定：控温热偶在段中点、误差 5 ℃；「10 ℃ 是上下各 5 ℃」；共用法兰处基准取两侧热偶读数的对数平均。
/// 物理把关人核过：基准只许用设定值算出的对数平均（LineSolver.ThermocoupleReferenceC），不许用模型算的交界管温。
///
/// 本档只用**合成的** SegmentOut／FlangeOut（手造的温度），毫秒级；验的是生产函数本身
/// （LineSolver.ThermocoupleReferenceC、ThermocoupleBasis.At、LineRunner.ThermocoupleChecks、Solver.PlateSlack），不手抄任何配方。
/// 合成数据的每个温度都故意取得互不相同，且把「接错一端」会读到的值放得很远 —— 读错端当场差几十 K。
/// </summary>
public class ThermocoupleBasisTests
{
    // ── 快门验数（出处：本工单规格，2026-09-14）：1150/1080 → 1114.71 °C，1080/1050 → 1064.94 °C，容差 0.01
    [Fact]
    public void 基准函数_端片取本段设定_共用片取开尔文对数平均()
    {
        var sp = new[] { 1150.0, 1080.0, 1050.0 };
        Assert.Equal(1150.0, LineSolver.ThermocoupleReferenceC(sp, 0, 3), 9);
        Assert.Equal(1114.71, LineSolver.ThermocoupleReferenceC(sp, 1, 3), 2);
        Assert.True(Math.Abs(LineSolver.ThermocoupleReferenceC(sp, 1, 3) - 1114.71) < 0.01);
        Assert.True(Math.Abs(LineSolver.ThermocoupleReferenceC(sp, 2, 3) - 1064.94) < 0.01);
        Assert.Equal(1050.0, LineSolver.ThermocoupleReferenceC(sp, 3, 3), 9);

        // 自证：必须是**开尔文**上的对数平均 —— 在摄氏上取会得 1114.63，差 0.08，超出 0.01 的容差，这条就抓得住
        double wrongC = (1150.0 - 1080.0) / Math.Log(1150.0 / 1080.0);
        Assert.True(Math.Abs(wrongC - 1114.71) > 0.05, $"摄氏对数平均 {wrongC:0.000} 与开尔文的分不开 —— 上面那条验不出温标写错");

        // T₁ = T₂ 取 T₁；对称；落在两者之间
        Assert.Equal(1100.0, LineSolver.LogMeanC(1100.0, 1100.0), 9);
        Assert.Equal(LineSolver.LogMeanC(1150, 1080), LineSolver.LogMeanC(1080, 1150), 9);
        double m = LineSolver.LogMeanC(1150, 1080);
        Assert.True(m > 1080 && m < 1150 && m < 0.5 * (1150 + 1080), "对数平均应落在两者之间且略低于算术平均");
        Assert.True(double.IsNaN(LineSolver.LogMeanC(double.NaN, 1080)));

        // 片号越界要当场响，不许静默返回一个数
        Assert.Throws<ArgumentOutOfRangeException>(() => LineSolver.ThermocoupleReferenceC(sp, 4, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => LineSolver.ThermocoupleReferenceC(sp, -1, 3));
        Assert.Throws<ArgumentException>(() => LineSolver.ThermocoupleReferenceC(new[] { 1150.0 }, 1, 3));
    }

    /// <summary>
    /// 三段四片的合成整线。每个温度都互不相同；接头两端的「错误那一端」离正确值几十 K。
    /// <code>
    ///   段   设定   首端(A)  末端(B)   基线A    基线B
    ///   HC1  1150   1146.0   1112.0   1147.5   1116.3
    ///   HC2  1080   1113.5   1071.5   1116.1   1065.2
    ///   HC3  1050   1064.5   1041.0   1065.0   1043.0
    ///   片        圆盘峰   舌片区峰
    ///   入口      1149.0   1152.0
    ///   HC1|HC2   1117.5   1110.0
    ///   HC2|HC3   1069.0   1060.0
    ///   出口      1040.0   1038.0
    /// </code>
    /// </summary>
    private static (SegmentOut[] Segs, FlangeOut[] Fl) Line()
    {
        var segs = new[]
        {
            new SegmentOut { Name = "HC1", SetpointC = 1150, TRootAC = 1146.0, TRootBC = 1112.0, BaseTRootAC = 1147.5, BaseTRootBC = 1116.3 },
            new SegmentOut { Name = "HC2", SetpointC = 1080, TRootAC = 1113.5, TRootBC = 1071.5, BaseTRootAC = 1116.1, BaseTRootBC = 1065.2 },
            new SegmentOut { Name = "HC3", SetpointC = 1050, TRootAC = 1064.5, TRootBC = 1041.0, BaseTRootAC = 1065.0, BaseTRootBC = 1043.0 },
        };
        var fl = new[]
        {
            new FlangeOut { Name = "入口",    TDiscMaxC = 1149.0, TTabMaxC = 1152.0 },
            new FlangeOut { Name = "HC1|HC2", TDiscMaxC = 1117.5, TTabMaxC = 1110.0, Shared = true },
            new FlangeOut { Name = "HC2|HC3", TDiscMaxC = 1069.0, TTabMaxC = 1060.0, Shared = true },
            new FlangeOut { Name = "出口",    TDiscMaxC = 1040.0, TTabMaxC = 1038.0 },
        };
        return (segs, fl);
    }

    private static double Ref(int j) => LineSolver.ThermocoupleReferenceC(new[] { 1150.0, 1080.0, 1050.0 }, j, 3);

    [Fact]
    public void 逐片热侧冷侧_共用接头读段jm1末端与段j首端_端片只有一侧()
    {
        var (segs, fl) = Line();

        // 入口：只有 HC1 首端；最热的是舌片区峰
        var t0 = ThermocoupleBasis.At(segs, fl, 0);
        Assert.Equal(1150.0, t0.RefC, 9);
        Assert.Equal("舌片区峰", t0.HottestWhat);
        Assert.Equal(1152.0 - 1150.0, t0.HotK, 9);
        Assert.Equal(1150.0 - 1146.0, t0.ColdK, 9);
        Assert.Equal("HC1 首端", t0.RootColdWhere);
        Assert.Equal(1147.5, t0.ModelJointC, 9);

        // HC1|HC2：两端 = HC1 末端 1112.0 与 HC2 首端 1113.5（读成 HC1 首端 1146 或 HC2 末端 1071.5 会差几十 K）
        var t1 = ThermocoupleBasis.At(segs, fl, 1);
        Assert.Equal(Ref(1), t1.RefC, 9);
        Assert.Equal(1113.5, t1.RootHotC, 9);
        Assert.Equal("HC2 首端", t1.RootHotWhere);
        Assert.Equal(1112.0, t1.RootColdC, 9);
        Assert.Equal("HC1 末端", t1.RootColdWhere);
        Assert.Equal("圆盘峰", t1.HottestWhat);
        Assert.Equal(1117.5 - Ref(1), t1.HotK, 9);
        Assert.Equal(Ref(1) - 1112.0, t1.ColdK, 9);
        Assert.Equal(0.5 * (1116.3 + 1116.1), t1.ModelJointC, 9);   // 基线也是同两端的平均

        // HC2|HC3：最热的是管根（HC2 末端 1071.5 高过圆盘峰 1069.0）
        var t2 = ThermocoupleBasis.At(segs, fl, 2);
        Assert.Equal("管根（HC2 末端）", t2.HottestWhat);
        Assert.Equal(1071.5 - Ref(2), t2.HotK, 9);
        Assert.Equal(Ref(2) - 1064.5, t2.ColdK, 9);
        Assert.Equal("HC3 首端", t2.RootColdWhere);

        // 出口：只有 HC3 末端
        var t3 = ThermocoupleBasis.At(segs, fl, 3);
        Assert.Equal(1050.0, t3.RefC, 9);
        Assert.Equal("管根（HC3 末端）", t3.HottestWhat);
        Assert.Equal(1041.0 - 1050.0, t3.HotK, 9);
        Assert.Equal(1050.0 - 1041.0, t3.ColdK, 9);
        Assert.Equal(1043.0, t3.ModelJointC, 9);
    }

    [Fact]
    public void 判据取最差片_限值只从LineCase读_说明里并列模型交界温度且不带代号()
    {
        var (segs, fl) = Line();
        fl[2].DiscZoneRule = "圆盘区按 r ≤ 30.0 mm 圈";            // 分区规则要一路带到判据说明里（R48DiscZoneByRadiusTests 同一条规矩）
        var c = new LineCase();
        // 单一来源：两条限值都 = 热偶误差 5 K（出处：用户 2026-09-14「10 ℃ 是上下各 5 ℃」）
        Assert.Equal(5.0, LineCase.ThermocoupleErrorK, 9);
        Assert.Equal(LineCase.ThermocoupleErrorK, c.HotOverTcMaxK, 9);
        Assert.Equal(LineCase.ThermocoupleErrorK, c.ColdUnderTcMaxK, 9);

        var (hot, cold) = LineRunner.ThermocoupleChecks(c, segs, fl);

        Assert.Equal(LineResult.Key.HotOverTc, hot.Name);
        Assert.Equal(CheckKind.HardSafety, hot.Kind);
        Assert.Equal("HC2|HC3", hot.Where);                      // 6.56 K 是四片里最热的
        Assert.Equal(1071.5 - Ref(2), hot.Actual, 9);
        Assert.False(hot.Ok);
        Assert.Contains("对数平均", hot.Note);
        Assert.Contains("模型算的无法兰交界管温", hot.Note);
        Assert.Contains("圆盘区按 r ≤ 30.0 mm 圈", hot.Note);
        Assert.Contains("管根（HC2 末端）", hot.Note);              // 最热的是谁要写出来

        Assert.Equal(LineResult.Key.ColdUnderTc, cold.Name);
        Assert.Equal(CheckKind.HardSafety, cold.Kind);
        Assert.Equal("出口", cold.Where);                          // 9.0 K 是四片里最冷的
        Assert.Equal(9.0, cold.Actual, 9);
        Assert.False(cold.Ok);
        Assert.Contains("HC3 末端", cold.Note);
        Assert.Contains("模型算的无法兰交界管温", cold.Note);

        // 说明会进界面（ToolTip）：不许有判据代号
        Assert.False(Criteria.HasCode(hot.Note), "热侧说明里有判据代号：" + hot.Note);
        Assert.False(Criteria.HasCode(cold.Note), "冷侧说明里有判据代号：" + cold.Note);

        // 限值确实是从 LineCase 读的：放宽到 7 / 10，同一组数就过
        c.HotOverTcMaxK = 7.0; c.ColdUnderTcMaxK = 10.0;
        var (hot2, cold2) = LineRunner.ThermocoupleChecks(c, segs, fl);
        Assert.True(hot2.Ok); Assert.Equal(7.0, hot2.Limit, 9);
        Assert.True(cold2.Ok); Assert.Equal(10.0, cold2.Limit, 9);
    }

    [Fact]
    public void 模型交界温度与基准差超过1度才写明()
    {
        var (segs, fl) = Line();
        // HC1|HC2：基线平均 1116.2 vs 基准 1114.71 ⇒ 差 +1.49 > 1 ⇒ 写明
        string n1 = ThermocoupleBasis.ModelJointNote(ThermocoupleBasis.At(segs, fl, 1));
        Assert.Contains("差超过", n1);
        Assert.Contains("判定按热偶读数算", n1);
        // HC2|HC3：基线平均 1065.1 vs 基准 1064.94 ⇒ 差 +0.16 ≤ 1 ⇒ 只并列，不写「差超过」
        string n2 = ThermocoupleBasis.ModelJointNote(ThermocoupleBasis.At(segs, fl, 2));
        Assert.Contains("模型算的无法兰交界管温", n2);
        Assert.DoesNotContain("差超过", n2);
        // 没有基线 ⇒ 照实说算不出
        segs[0].BaseTRootBC = double.NaN;                          // HC1|HC2 的一端（HC1 末端）没有基线
        Assert.Contains("算不出", ThermocoupleBasis.ModelJointNote(ThermocoupleBasis.At(segs, fl, 1)));
        Assert.DoesNotContain("算不出", ThermocoupleBasis.ModelJointNote(ThermocoupleBasis.At(segs, fl, 0)));   // 入口不读那一端
    }

    [Fact]
    public void 任何一片判不了_整条判不了并点名()
    {
        var (segs, fl) = Line();
        fl[1].TDiscMaxC = double.NaN;                              // HC1|HC2 圆盘区空
        var (hot, cold) = LineRunner.ThermocoupleChecks(new LineCase(), segs, fl);
        Assert.True(hot.Undetermined); Assert.False(hot.Ok);
        Assert.Contains("HC1|HC2", hot.Where);
        Assert.Contains("圆盘区没有单元", hot.Note);
        Assert.False(cold.Undetermined);                           // 冷侧不吃圆盘峰，照判

        (segs, fl) = Line();
        segs[2].TRootBC = double.NaN;                              // 出口那一端管根算不出
        (hot, cold) = LineRunner.ThermocoupleChecks(new LineCase(), segs, fl);
        Assert.True(cold.Undetermined); Assert.Equal("出口", cold.Where);
        Assert.True(hot.Undetermined);                             // 热侧也要读管根较热端
        Assert.True(double.IsNaN(ThermocoupleBasis.At(segs, fl, 3).ColdK));
        Assert.False(double.IsNaN(ThermocoupleBasis.At(segs, fl, 2).ColdK));   // 别的片不受牵连
    }

    [Fact]
    public void 求解器逐片裕度走同一份读数_旧判法没有逐片裕度()
    {
        var (segs, fl) = Line();
        var r = new LineResult { Segments = segs, Flanges = fl };
        for (int j = 0; j < 4; j++)
        {
            var t = ThermocoupleBasis.At(r, j);
            Assert.Equal(5.0 - t.HotK, Solver.PlateSlack(r, LineResult.Key.HotOverTc, j, 5.0, 5.0), 9);
            Assert.Equal(4.0 - t.ColdK, Solver.PlateSlack(r, LineResult.Key.ColdUnderTc, j, 4.0, 5.0), 9);   // 参数顺序：冷侧限值在前
        }
        // 判不了 ⇒ NaN（调用方当成不过）
        fl[0].TTabMaxC = double.NaN;
        Assert.True(double.IsNaN(Solver.PlateSlack(r, LineResult.Key.HotOverTc, 0, 5, 5)));
        // 旧判法降为参考量 ⇒ 不许再给逐片裕度（传进来当场响）
        Assert.Throws<ArgumentOutOfRangeException>(() => Solver.PlateSlack(r, LineResult.Key.DiscTemp, 1, 5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Solver.PlateSlack(r, LineResult.Key.FlangeDip, 1, 5, 5));
        // 分派表与复核只追新判法
        var keys = Solver.Allocation.Select(a => a.Key).ToArray();
        Assert.Contains(LineResult.Key.HotOverTc, keys);
        Assert.Contains(LineResult.Key.ColdUnderTc, keys);
        Assert.DoesNotContain(LineResult.Key.DiscTemp, keys);
        Assert.DoesNotContain(LineResult.Key.FlangeDip, keys);
        Assert.Equal(new[] { LineResult.Key.NetFlux, LineResult.Key.ColdUnderTc, LineResult.Key.HotOverTc }, SensitivityMatrix.Keys);
    }

    [Fact]
    public void 必备名单与对照表_新两条是硬线_旧两条是参考量()
    {
        // K 路（2026-09-15，Opus 5）：有意改动 —— 名单加工况维。带玻璃稳态：新两条是硬线；空管到温稳态：两条都只作参考（用户 2026-09-15「空管时铂过热那条也不该按 5 ℃ 卡，只要J<11即可」；
        //   冷侧归属是主会话按原话的解读）。旧两条两态都不在名单里。依据 LineResult.RequiredByState。
        var req = LineResult.RequiredFor(emptyTube: false).ToDictionary(q => q.Prefix, q => q.Kind);
        Assert.Equal(CheckKind.HardSafety, req[LineResult.Key.HotOverTc]);
        Assert.Equal(CheckKind.HardSafety, req[LineResult.Key.ColdUnderTc]);   // 冷侧升为硬线（旧判法是「目标」）
        Assert.False(req.ContainsKey(LineResult.Key.DiscTemp));
        Assert.False(req.ContainsKey(LineResult.Key.FlangeDip));
        var reqE = LineResult.RequiredFor(emptyTube: true).ToDictionary(q => q.Prefix, q => q.Kind);
        Assert.False(reqE.ContainsKey(LineResult.Key.HotOverTc));
        Assert.False(reqE.ContainsKey(LineResult.Key.ColdUnderTc));
        Assert.False(reqE.ContainsKey(LineResult.Key.DiscTemp));
        Assert.False(reqE.ContainsKey(LineResult.Key.FlangeDip));
        Assert.Equal(CheckKind.Reference, LineResult.StateKindOf(LineResult.Key.HotOverTc, emptyTube: true));
        Assert.Equal(CheckKind.Reference, LineResult.StateKindOf(LineResult.Key.ColdUnderTc, emptyTube: true));

        // 2026-09-14 Opus 5（复审）：代号**不换主人** —— 新两条用新代号 ⑦／⑧，②″／③ 永远指旧判法那两条（历史记录、按代号前缀取值的地方都不会悄悄换量）
        Assert.Equal(LineResult.Key.HotOverTc, Criteria.Of("⑦")!.Key);
        Assert.Equal(LineResult.Key.ColdUnderTc, Criteria.Of("⑧")!.Key);
        Assert.Equal(LineResult.Key.DiscTemp, Criteria.Of("②″")!.Key);
        Assert.Equal(LineResult.Key.FlangeDip, Criteria.Of("③")!.Key);
        Assert.Null(Criteria.Of("·②″"));
        Assert.Null(Criteria.Of("·③"));
        Assert.True(Criteria.Of("⑦")!.Hard && Criteria.Of("⑧")!.Hard);
        Assert.False(Criteria.Of("②″")!.Hard || Criteria.Of("③")!.Hard);
        // 名字里不许有「口径」这类内部词（会上界面；ShellThermal 那条规矩）
        foreach (var k in new[] { LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc, LineResult.Key.DiscTemp, LineResult.Key.FlangeDip })
            Assert.DoesNotContain("口径", k);
        Assert.Equal("圆盘区最高温 − 管温（旧判法）", Criteria.Plain(LineResult.Key.DiscTemp));
        Assert.Equal("法兰增量温降（旧判法）", Criteria.Plain(LineResult.Key.FlangeDip));

        // 说明文字写意图：热偶在段中点、对数平均、并列模型交界温度。
        // ★ U 路（2026-09-18，Opus 5）：限值改成参数表里的输入（默认 = 热偶在 1100 °C 的误差）⇒ 全表说明里**一个限值数字都不许印**
        //   （同一张表会被不同预算的算例共用，印了就会出现「印出来的 ≠ 判的」）；改成指到参数表那两项。
        string hotMeans = Criteria.Of("⑦")!.Means, coldMeans = Criteria.Of("⑧")!.Means;
        Assert.Contains("段中点", hotMeans);
        Assert.Contains("最热铂高出热偶读数 允许值", hotMeans);
        Assert.Contains("管根低于热偶读数 允许值", coldMeans);
        Assert.DoesNotContain($"{LineCase.ThermocoupleErrorK:0} ℃", hotMeans);
        Assert.DoesNotContain($"{LineCase.ThermocoupleErrorK:0} ℃", coldMeans);
        Assert.Contains("对数平均", hotMeans);
        Assert.Contains("模型算的无法兰交界管温", hotMeans);
        Assert.Contains("对数平均", coldMeans);
        Assert.False(Criteria.HasCode(hotMeans) || Criteria.HasCode(coldMeans));
        // 「差超过几度才写明」的阈值只从 ThermocoupleBasis.ModelGapNoteK 读（2026-09-14 Opus 5 复审：原来四处写死「1 ℃」）
        string gap = $"差超过 {ThermocoupleBasis.ModelGapNoteK:0} ℃";
        Assert.Contains(gap, hotMeans);
        Assert.Contains(gap, coldMeans);
        Assert.Contains($"与基准差超过 {ThermocoupleBasis.ModelGapNoteK:0} ℃", Criteria.Html());
        foreach (var (dir, f) in new[] { ("Core", "Criteria.cs"), ("UI", "ManualPage.cs") })
        {
            string src = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", dir, f));
            Assert.DoesNotContain("差超过 1 ℃", src);
            Assert.Contains("ThermocoupleBasis.ModelGapNoteK", src);
        }
        // 说明书不再说「管孔净流入」与新热侧是同一条线的两个视角（新热侧的基准是设定值，还算舌片区峰与管根较热端，两者推不出彼此）
        Assert.DoesNotContain("「管孔净流入」与「最热铂高出热偶读数」是同一条安全线的两个视角", Criteria.Html());
    }

    [Fact]
    public void 安装报告_逐片列热偶基准并写明差超过1度的片_参考量不印过()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0, 1050.0 }; d.SegLengthMm = new[] { 300.0, 300.0, 300.0 }; d = d.Fit();
        var p = new DesignInputs();
        var (segs, fl) = Line();
        var lc = new LineCase();
        var (hot, cold) = LineRunner.ThermocoupleChecks(lc, segs, fl);
        var r = new LineResult
        {
            Ok = true, Converged = true, Segments = segs, Flanges = fl,
            Checks = new[]
            {
                hot, cold,
                new ConstraintOut { Name = LineResult.Key.FlangeDip, Actual = 3.2, Limit = lc.RootDeltaMaxK, Unit = "K", Ok = true, Kind = CheckKind.Reference, Where = "HC2" },
            },
        };
        string t = InstallReport.Build(r, d, p, "", new DateTime(2026, 9, 14, 12, 0, 0));
        Assert.Contains("热偶读数基准", t);
        Assert.Contains("对数平均", t);
        Assert.Contains("HC1|HC2：模型算的无法兰交界管温", t);     // 差 +1.49 ℃ ⇒ 逐片写明
        Assert.DoesNotContain("HC2|HC3：模型算的无法兰交界管温", t); // 差 +0.16 ℃ ⇒ 只在表里并列
        Assert.Contains("参考（不卡交付）", t);
        // 判据代号不许出现（「③ 结果与出图」是页签名，不是判据代号 ⇒ 按「代号紧跟判据名」查，同 NoCriterionCodeInUiTests 的判法）
        foreach (var bad in new[] { "②′", "②″", "⑦", "⑧", "③ 法兰", "③法兰" }) Assert.DoesNotContain(bad, t);
    }

    /// <summary>
    /// 2026-09-14 Opus 5（复审）：ThermocoupleChecks 是公开函数 —— 片数与段数对不上时，两条都要报「判不了」，
    /// 不许只判传进来的那几片、却照样给出「过／不过」（缺的片一次都没判过）。
    /// </summary>
    [Fact]
    public void 片数与段数对不上_两条都判不了()
    {
        var (segs, fl) = Line();
        var shortFl = fl.Take(3).ToArray();                        // 3 段只给 3 片（少了出口）
        var (hot, cold) = LineRunner.ThermocoupleChecks(new LineCase(), segs, shortFl);
        Assert.True(hot.Undetermined); Assert.False(hot.Ok);
        Assert.True(cold.Undetermined); Assert.False(cold.Ok);
        Assert.Contains("法兰片数 3 与段数 3 对不上", hot.Note);
        Assert.Equal(LineResult.Key.HotOverTc, hot.Name);
        Assert.Equal(LineResult.Key.ColdUnderTc, cold.Name);
        // 自证：同一组数片数对上时是判得了的（不然上面验的是别的原因）
        var (hot2, cold2) = LineRunner.ThermocoupleChecks(new LineCase(), segs, fl);
        Assert.False(hot2.Undetermined); Assert.False(cold2.Undetermined);
        // 多给一片同样判不了
        var longFl = fl.Concat(new[] { new FlangeOut { Name = "多余", TDiscMaxC = 1000, TTabMaxC = 1000 } }).ToArray();
        Assert.True(LineRunner.ThermocoupleChecks(new LineCase(), segs, longFl).Hot.Undetermined);
        Assert.True(LineRunner.ThermocoupleChecks(new LineCase(), Array.Empty<SegmentOut>(), Array.Empty<FlangeOut>()).Cold.Undetermined);
    }

    /// <summary>
    /// 2026-09-14 Opus 5（复审）：FlangeAutoSizer.CloneCase 带上四个判据限值（R48 B 那次加的）。
    /// 此前一个都不带 ⇒ 改过限值的算例在图纸路径定尺寸／加密复算的副本里静默退回默认值；这一改是行为变化，没有门就会被人删掉。
    /// </summary>
    [Fact]
    public void 算例副本带上四个判据限值()
    {
        var lc0 = new LineCase { HotOverTcMaxK = 3.5, ColdUnderTcMaxK = 4.25, DiscOverTempMaxK = 7.5, RootDeltaMaxK = 12.5 };
        // 自证：四个值都不是默认值，否则「副本等于原值」验不出东西
        var dflt = new LineCase();
        Assert.NotEqual(dflt.HotOverTcMaxK, lc0.HotOverTcMaxK);
        Assert.NotEqual(dflt.ColdUnderTcMaxK, lc0.ColdUnderTcMaxK);
        Assert.NotEqual(dflt.DiscOverTempMaxK, lc0.DiscOverTempMaxK);
        Assert.NotEqual(dflt.RootDeltaMaxK, lc0.RootDeltaMaxK);
        var lc = FlangeAutoSizer.CloneCase(lc0);
        Assert.Equal(3.5, lc.HotOverTcMaxK, 12);
        Assert.Equal(4.25, lc.ColdUnderTcMaxK, 12);
        Assert.Equal(7.5, lc.DiscOverTempMaxK, 12);
        Assert.Equal(12.5, lc.RootDeltaMaxK, 12);
    }

    /// <summary>限值单一来源的源码门（照 SingleSourceLimitTests）：求解器与敏感度矩阵读的是新限值字段，不写常数。</summary>
    [Fact]
    public void 求解器限值只从LineCase的新字段读()
    {
        string root = HandoverDoc.Root();
        string solver = File.ReadAllText(Path.Combine(root, "Pt_Optimize", "Core", "Solver.cs"));
        string sens = File.ReadAllText(Path.Combine(root, "Pt_Optimize", "Core", "SensitivityMatrix.cs"));
        Assert.Contains("lc.ColdUnderTcMaxK", solver);
        Assert.Contains("lc.HotOverTcMaxK", solver);
        Assert.Contains("lc0.ColdUnderTcMaxK", sens);
        Assert.Contains("lc0.HotOverTcMaxK", sens);
        Assert.DoesNotContain("coldMax = 5", solver);
        Assert.DoesNotContain("hotMax = 5", solver);
    }
}
