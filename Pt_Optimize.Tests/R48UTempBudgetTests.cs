using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ U 路（2026-09-18，Opus 5）：**温差预算是工程师填的，不是写死的 5。**
///
/// 用户 2026-09-17：「简单说：按半毫米一层缠，现在的判据下没有能造的设计……要你定一件事：冷侧那 5 度能不能放宽」；
/// 2026-09-18：「OK!了解了，请开工」。本路做的是**接线**，不替谁改数：
///   · 参数表加两项「管根低于热偶读数 允许值 [K]」「最热铂高出热偶读数 允许值 [K]」，默认 5.0，说明里写出处；
///   · 判据表、保温搜索、加密复算容差、说明书、安装报告**一律读这两项**，别处不许再有第二个 5。
///
/// 本档的四道门：
///   ① 默认 5 时全链路逐位不变（默认值、算例限值、复核容差、判词里的限值栏）——「改了没走样」的证据；
///   ② 填成别的数时判据裕度**按定义跟着变**（热侧 7、冷侧 12：同一份合成整线由「不过」翻成「过」，限值栏印新数）；
///   ③ 源码门：热侧／冷侧的限值不许在判据、保温搜索、复核容差、说明书里写死 —— <see cref="LineCase.ThermocoupleErrorK"/>
///      只许出现在「它自己的定义」与「两项输入的默认值」这三处，改回写死 ⇒ 红；
///   ④ 界面门：两项真的在参数表里可见可改，且归到一个对整线链有效的分类下（不是「只读」那两组）。
///
/// ⚠ 全档毫秒级：热侧／冷侧两条用生产函数 <see cref="LineRunner.ThermocoupleChecks"/> 配**合成**的段／片温度
///   （与 <see cref="ThermocoupleBasisTests"/> 同一套合成数），不跑分钟级的整线解，也不手抄任何生产配方。
/// </summary>
public class R48UTempBudgetTests
{
    // 与 ThermocoupleBasisTests.Line() 同一套合成整线（三段四片）。故意各不相同，读错一端差几十 K。
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

    private static string Src(string rel) => File.ReadAllText(Path.Combine(HandoverDoc.Root(), rel.Replace('/', Path.DirectorySeparatorChar)));

    // ══════════════════════════════════════════════════════════════════════
    //  ① 默认 5 ⇒ 全链路逐位不变
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    public void 默认预算仍是五度_从参数表一路到判据与复核容差都逐位不变()
    {
        var p = new DesignInputs();
        Assert.Equal(5.0, LineCase.ThermocoupleErrorK, 12);
        Assert.Equal(LineCase.ThermocoupleErrorK, p.ColdUnderTcAllowK, 12);
        Assert.Equal(LineCase.ThermocoupleErrorK, p.HotOverTcAllowK, 12);

        // 整线算例：没显式设过 ⇒ 读参数表
        var lc = DesignSpec.W08.Clone().BuildCase(p, checkRamp: false);
        Assert.Equal(5.0, lc.HotOverTcMaxK, 12);
        Assert.Equal(5.0, lc.ColdUnderTcMaxK, 12);
        Assert.Equal(5.0, new LineCase().HotOverTcMaxK, 12);
        Assert.Equal(5.0, new LineCase().ColdUnderTcMaxK, 12);

        // 加密复算容差：默认下仍是 0.5／0.5／0.5（K 路那三列，逐位不变）
        var tol = MeshVerify.TolTemplate(new LineCase());
        Assert.Equal(0.5, tol.First(t => t.Name == Criteria.Plain(LineResult.Key.NetFlux)).Tol, 12);
        Assert.Equal(0.5, tol.First(t => t.Name == Criteria.Plain(LineResult.Key.HotOverTc)).Tol, 12);
        Assert.Equal(0.5, tol.First(t => t.Name == Criteria.Plain(LineResult.Key.ColdUnderTc)).Tol, 12);

        // 判词里的限值栏与判定：这份合成整线在 5 K 下热侧 6.56、冷侧 9.00，两条都不过（与 R48 B 那道门同一组数）
        var (segs, fl) = Line();
        var (hot, cold) = LineRunner.ThermocoupleChecks(new LineCase { Base = p }, segs, fl);
        Assert.Equal(5.0, hot.Limit, 12);
        Assert.Equal(5.0, cold.Limit, 12);
        Assert.False(hot.Ok);
        Assert.False(cold.Ok);
        Assert.Equal("HC2|HC3", hot.Where);
        Assert.Equal("出口", cold.Where);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ② 填成别的数 ⇒ 判据裕度按定义跟着变
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    public void 填成别的数时判据限值与复核容差都跟着走()
    {
        var (segs, fl) = Line();
        double hot5 = LineRunner.ThermocoupleChecks(new LineCase(), segs, fl).Hot.Actual;
        double cold5 = LineRunner.ThermocoupleChecks(new LineCase(), segs, fl).Cold.Actual;

        // 冷侧 9.00 K：预算填 12 ⇒ 由「不过」翻成「过」，值一位不动（放宽的是限值，不是数）
        var wide = new DesignInputs { HotOverTcAllowK = 7.0, ColdUnderTcAllowK = 12.0 };
        var lc = DesignSpec.W08.Clone().BuildCase(wide, checkRamp: false);
        Assert.Equal(7.0, lc.HotOverTcMaxK, 12);
        Assert.Equal(12.0, lc.ColdUnderTcMaxK, 12);

        var (hot, cold) = LineRunner.ThermocoupleChecks(new LineCase { Base = wide }, segs, fl);
        Assert.Equal(7.0, hot.Limit, 12);
        Assert.Equal(12.0, cold.Limit, 12);
        Assert.Equal(hot5, hot.Actual, 12);      // 判据值与预算无关
        Assert.Equal(cold5, cold.Actual, 12);
        Assert.True(hot.Ok, $"热侧 {hot.Actual:0.000} K 在 7 K 预算下应当过");
        Assert.True(cold.Ok, $"冷侧 {cold.Actual:0.000} K 在 12 K 预算下应当过");
        // 收紧到判据值以下 ⇒ 立刻翻回「不过」（自证：不是恒过）
        var tight = new DesignInputs { HotOverTcAllowK = 1.0, ColdUnderTcAllowK = 1.0 };
        var (hotT, coldT) = LineRunner.ThermocoupleChecks(new LineCase { Base = tight }, segs, fl);
        Assert.False(hotT.Ok);
        Assert.False(coldT.Ok);

        // 复核容差 = 各自限值的 10 %，跟着预算走
        var tol = MeshVerify.TolTemplate(new LineCase { Base = wide });
        Assert.Equal(0.7, tol.First(t => t.Name == Criteria.Plain(LineResult.Key.HotOverTc)).Tol, 12);
        Assert.Equal(1.2, tol.First(t => t.Name == Criteria.Plain(LineResult.Key.ColdUnderTc)).Tol, 12);
        Assert.Equal(0.5, tol.First(t => t.Name == Criteria.Plain(LineResult.Key.NetFlux)).Tol, 12);   // 净流入与预算无关

        // 判词里要写出本次实际用的那个数，不许印默认值
        Assert.Contains("7", hot.Note);
        Assert.Contains("12", cold.Note);
        Assert.DoesNotContain("误差 5 ℃", hot.Note);
        Assert.DoesNotContain("误差 5 ℃", cold.Note);
    }

    [Fact]
    public void 安装报告与说明书印的是本次填的预算_不是默认值()
    {
        var wide = new DesignInputs { HotOverTcAllowK = 7.0, ColdUnderTcAllowK = 12.0 };
        // 安装报告的热偶基准段（只读结果与设计，不重算）
        string rep = Src("Pt_Optimize/Core/InstallReport.cs");
        Assert.Contains("lc.HotOverTcMaxK", rep);
        Assert.Contains("lc.ColdUnderTcMaxK", rep);
        Assert.DoesNotContain("LineCase.ThermocoupleErrorK", rep);

        // 保温搜索报告：限值从整线算例读（两个数都从 LimitsOf 来），不许再插默认值
        string ins = Src("Pt_Optimize/Core/InsulationSearch.cs");
        Assert.DoesNotContain("{LineCase.ThermocoupleErrorK", ins);

        // 说明书：限值那张表挂**当前参数表**那一份算例
        string man = Src("Pt_Optimize/UI/ManualPage.cs");
        Assert.Contains("var lim = new LineCase { Base = dfl };", man);
        Assert.DoesNotContain("<b>这是程序里的常数，参数表里没有这一项</b>", man);   // 说明书正文里那句已经不成立
        Assert.Contains("在「① 输入」页最上面那组", man);
        var lim = new LineCase { Base = wide };
        Assert.Equal(7.0, lim.HotOverTcMaxK, 12);
        Assert.Equal(12.0, lim.ColdUnderTcMaxK, 12);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ③ 源码门：不许改回写死
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    public void 源码门_热冷两条限值不许在别处写死()
    {
        // 判据构造只从算例读
        string lr = Src("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("c.HotOverTcMaxK", lr);
        Assert.Contains("c.ColdUnderTcMaxK", lr);
        Assert.Contains("Base is not null ? Base.HotOverTcAllowK : ThermocoupleErrorK", lr);
        Assert.Contains("Base is not null ? Base.ColdUnderTcAllowK : ThermocoupleErrorK", lr);
        // 改回「字段 = 常数」当场红
        Assert.DoesNotContain("public double HotOverTcMaxK = ", lr);
        Assert.DoesNotContain("public double ColdUnderTcMaxK = ", lr);

        // ★ 全仓扫：ThermocoupleErrorK **只许当「默认值」用，不许当限值或容差用**。
        //   凡是算限值、算容差、印限值那一列的地方，数必须从算例来（LineCase.HotOverTcMaxK／ColdUnderTcMaxK）——
        //   在那里引用这个常数，就是又抄了一份限值，也就是「印出来的 ≠ 判的」的起点。
        //   放行的只有四处定义／兜底（下面逐条写死），外加**显示文字里明写「默认」二字**的解释句
        //   （那种句子旁边一定同时印着算例的真实限值，两个数并列，读的人分得清哪个是判的）。
        var allowed = new[]
        {
            ("Pt_Optimize/Core/LineRunner.cs", "public const double ThermocoupleErrorK = 5.0;"),
            ("Pt_Optimize/Core/LineRunner.cs", "Base is not null ? Base.HotOverTcAllowK : ThermocoupleErrorK"),
            ("Pt_Optimize/Core/LineRunner.cs", "Base is not null ? Base.ColdUnderTcAllowK : ThermocoupleErrorK"),
            ("Pt_Optimize/Core/DesignInputs.cs", "public double ColdUnderTcAllowK { get; set; } = LineCase.ThermocoupleErrorK;"),
            ("Pt_Optimize/Core/DesignInputs.cs", "public double HotOverTcAllowK { get; set; } = LineCase.ThermocoupleErrorK;"),
        };
        var offenders = new System.Collections.Generic.List<string>();
        foreach (string full in Directory.GetFiles(Path.Combine(HandoverDoc.Root(), "Pt_Optimize"), "*.cs", SearchOption.AllDirectories)
                                        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            string name = Path.GetRelativePath(HandoverDoc.Root(), full).Replace('\\', '/');
            foreach (string line in File.ReadAllText(full).Split('\n'))
            {
                if (!line.Contains("ThermocoupleErrorK")) continue;
                string t = line.Trim();
                if (t.StartsWith("//")) continue;          // 注释里说出处可以（/// 也以 // 开头）
                if (allowed.Any(a => a.Item1 == name && t.Contains(a.Item2))) continue;
                if (t.Contains("默认")) continue;                 // 显示文字里明写「默认」＋并列印算例限值
                offenders.Add($"{name}：{t}");
            }
        }
        Assert.True(offenders.Count == 0,
            "热侧／冷侧的限值默认值被抄到了别处（印出来的迟早 ≠ 判的）：\n  " + string.Join("\n  ", offenders));

        // 复核容差是「限值的比例」，不是写死的 0.5
        string mv = Src("Pt_Optimize/Core/MeshVerify.cs");
        Assert.Contains("c => TcMeshTolFrac * c.HotOverTcMaxK", mv);
        Assert.Contains("c => TcMeshTolFrac * c.ColdUnderTcMaxK", mv);
        Assert.Equal(0.1, MeshVerify.TcMeshTolFrac, 12);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ④ 界面门：参数表里真的看得见、改得了
    // ══════════════════════════════════════════════════════════════════════
    [Theory]
    [InlineData("ColdUnderTcAllowK", "管根低于热偶读数 允许值 [K]")]
    [InlineData("HotOverTcAllowK", "最热铂高出热偶读数 允许值 [K]")]
    public void 界面门_两项在参数表里可见可改且写了出处(string prop, string display)
    {
        var pi = typeof(DesignInputs).GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(pi);
        Assert.True(pi!.CanRead && pi.CanWrite, $"{prop} 要能改 —— 只读的输入等于没有这一项");
        Assert.NotEqual(false, pi.GetCustomAttribute<BrowsableAttribute>()?.Browsable);
        Assert.Equal(display, pi.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName);

        string cat = pi.GetCustomAttribute<CategoryAttribute>()?.Category ?? "";
        Assert.Equal(ParamCat.判据限值与窗口, cat);
        Assert.DoesNotContain("只读", cat);                      // 不许落进「改了不起作用」那两组

        string desc = pi.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        Assert.Contains("1100 °C", desc);                        // 出处：用户 2026-09-14「圆盘区 5 K = 热偶 1100 °C 误差」
        Assert.Contains("热偶", desc);
        Assert.Contains("默认 5", desc);
        Assert.False(Criteria.HasCode(display), "界面上不许出现判据代号");
        Assert.False(Criteria.HasCode(desc), "界面上不许出现判据代号");
        Assert.DoesNotContain("--", desc);                        // 界面上不许出现命令行开关名
    }

    [Fact]
    public void 界面门_新分类登记在参数分区与两格阶段里()
    {
        Assert.Contains(PtOptimize.UI.Flow.Params, x => x.CategoryPrefix == ParamCat.判据限值与窗口);
        foreach (var st in new[] { PtOptimize.UI.StageId.输入, PtOptimize.UI.StageId.整线核算 })
            Assert.Contains(ParamCat.判据限值与窗口, PtOptimize.UI.Flow.Stage(st).ParamCategoryPrefixes);
        // 分类名不许带判据代号或开关名
        Assert.False(Criteria.HasCode(ParamCat.判据限值与窗口));
        Assert.DoesNotContain("--", ParamCat.判据限值与窗口);
    }
}
