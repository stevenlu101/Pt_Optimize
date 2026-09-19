using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 G2 前后对照探针（2026-09-15，Opus 5 写；规格出自常驻物理把关人第十一轮；复审同日重写；复审二同日再改）：
/// **升温两节点补铜排通道与舌片保温、法兰节点四项逐项标定，前后差多少、各项热流是什么、与准静态壳解对不对得上。**
///
/// ══ 设置
///   B2：第二批 B2 的设计（管保温 7.5、逐片圆盘 10.5/3.5/5.0/10.0、逐片舌 5.0/3.5/5.0/10.5、SizeTongues、共用片抽热各半、耦合容差 0.25 K），**导航网格**整线一次；
///   误差预算：误差预算设计（R48ClampRecipeImpactTests.Design 同一组输入，默认耦合容差）—— 旧 22.05 K／144.29 K 就是在它上面量的。
///   ★ 复审二（审查意见 major「探针只在带玻璃稳态上标定」）：两个设计各跑**带玻璃**与**空管**两份（空管 = BuildCase(p, checkRamp: false, emptyTube: true, EmptyTubeSetpoint.RampTarget)，
///     控温点全线升到升温目标 —— 用户已定的空管到温稳态，判据表空管那张表的升温行就用这个场标定），并列交物理把关人；
///     另加**现役锚点**（审查意见 minor）：几何同 Program.cs「--flangestab」里的现役对照（Ø120／舌 200×半宽 40／厚 2.0／管纤维 2.5／圆盘保温 2.5／舌裸／舌端不接铜排），
///     走 LineRunner.Run + FlangeLumped，印能不能标定、各版峰值。
///     ⚠ +215 K（HANDOVER §4.2s，用户 2026-08-25 确认比较符合现状）是「--ramp2」那条输入路径算的（图纸几何、按许用 J 定电流、1050 °C 电流场的参考电阻、不解稳态热场），
///       与本路径输入不同 ⇒ 不要求复现，只并列。
///   改前逐位记录：deliverable/R48_G2_改前基线_2026-09-15.txt（甲、乙、整片热稳定几何导度与裕度）；
///   第一版逐位记录：deliverable/R48_G2_升温两节点补铜排通道_B2_2026-09-15.txt 与 …_误差预算_2026-09-15.txt（丙、第一版整片热稳定裕度）；
///   复审逐位记录：deliverable/R48_G2_复审_升温两节点逐项标定_B2_2026-09-15.txt 与 …_误差预算_2026-09-15.txt（丁峰值、复审整片热稳定裕度）。
///   本次输出另写 deliverable/R48_G2_复审二_升温两节点_{B2,误差预算}_{带玻璃,空管}_2026-09-15.txt 与 …_现役锚点_2026-09-15.txt（不覆盖前两版）。
///
/// ══ 升温期参考项的版本（最不利片，全部由生产函数 LineRunner.FlangeLumped + RampTwoNode.Solve 给；老口径 = 同一份输入把新项关掉）
///   甲　A 路前口径：不排除压接格、无铜排通道、舌片裸铂、参考电阻配管根温度、表面不标定、孔侧几何式；
///   乙　A 路后缺通道：排除压接格，其余同甲；乙1 只补舌片保温；乙2 只补铜排通道；
///   丙　G2 第一版：铜排通道 + 舌片保温，参考电阻仍配管根温度、表面不标定、孔侧几何式；
///   丙+ 丙再把发热标定到节点口径；丁′ 再标定表面散热（孔侧仍几何式）；
///   丁　复审生产：四项都在节点口径上标定（发热、表面、铜排、孔侧导度）—— 管孔流向与温差反号时标定不了、丁不解。
///   「关掉」用的老值：参考电阻 = FlangeLumpedOut.LegacyRefOhm、参考温度 = FlangeOut.TRootC、表面标定点与孔侧导度置 NaN、铜排导度置 0 —— J0 逐位核它们。
///
/// ══ 准静态壳解复核（不依赖集总假设的对照）
///   升温 20 K/h 全程约 56 h，法兰热时间常数分钟级 ⇒ 每一时刻法兰都近乎稳态。在丁的轨迹上（丁没解时用丙的轨迹，输出写明）取管温 300/500/700/900/1100 °C 附近、峰值时刻
///   与（复审二加）轨迹终点，用同一片网格做壳热解：电流场按电流线性缩放、管孔温度 = 该时刻管温、夹持温度按同一个分压假设与壳解自己的节点均温迭代到自洽，
///   其余边界与物性全取 LineRunner.PlateThermalInputs，壳热解调 LineRunner.SolvePlateThermal，节点各项调 RampTwoNode.CalibrateNode（与集总同一份节点格）。
///   表一：状态对照；表二：集总各项取在壳解节点均温上（状态相同只比方程）；表三（复审二加）：壳解最热格在哪。
///   ⚠ 壳解的管孔是定温边界（管温直接加在孔边，没串管壁翅片导度），管来绝对值会比集总大，只看方向。
///
/// ══ 跑前写死的判读（复审二，2026-09-15 Opus 5；复审的判读原样留在复审输出里，J1、J2′ 规则一字未改）
///   J0 回归（断言，只对有记录的两跑：B2 带玻璃、误差预算带玻璃）：甲、乙、丙三版峰值与记录逐位相同；整片热稳定的舌片几何导度、几何导度裕度与改前记录逐位相同；
///      第一版口径（夹持取 G）的裕度与第一版记录逐位相同 —— 复审二改调 LineRunner.StabilityCheck(…, G).Margin 复现（审查意见 minor：原手抄 (表面 + G + 管孔) ÷ 发热）；
///      **复审**的丁峰值与整片热稳定裕度与复审记录逐位相同 —— 本轮只改界面文字、显示位与探针，不改算术；不同 ⇒ 本轮动了算术，本探针其余结论作废。
///   J3 场对账（断言，丁解出来的每一跑）：最不利片、生产输入，在标定点上 发热／表面／铜排 与场的节点账相对差 ≤ 1e-9、管来 ≤ 1e-6、热容吸收与场能量残差之差 ≤ 1e-6 × 发热
///      （与快门 R48G2RampClampChannelGateTests 五同一组容差，依据写在那里）。有记录的两跑丁必须解得出（复审时解得出）。
///   J1（描述）：丁的「法兰 − 管」峰值 &gt; 100 K ⇒「未修好」；≤ 100 K ⇒ 只说量级过了物理把关人跑前门槛 —— 判据表那一行照样暂不给数、待复核。
///   J2′（描述）：升温末段（轨迹上管温最接近 1100 °C 的取点，**不含**复审二加的终点）|集总法兰温度 − 壳解节点均温| ≤ 30 K 且 集总管来与壳解管孔同向 ⇒「末段集总与壳解一致」；否则「不一致」。
///      30 K 沿用第一版 J2 的推导（J1 判别线 100 K 的三分之一）；同向是符号比较，没有门槛。（复审时如实写过：这条是看过第一版数据之后、跑复审之前写的。）
///   以下复审二新加的只描述，**没有门槛、不下判读**：
///   · 空管两跑、现役锚点：印同样的各版峰值与表一～表三；
///   · 轨迹终点取点（积分停下那一刻，管温约升温目标）：表里标「轨迹终点」；轨迹描述行分「不含终点（与复审同口径）」与「含终点」两句；
///   · 表三 壳解最热格：坐标、r、分区（与 ShellThermal 同一规则：r ≤ 盘半径为圆盘区）、离管孔 mm、当地厚度、电流密度、在哪种保温下、是不是被边界钉住的格；并列圆盘区峰值与舌片区峰值的位置；
///   · 切线导度与发热偏置（审查建议的改法，交物理把关人定）：在标定点上调 LineRunner.SolvePlateThermal，管根温度、夹持温度（定温边界才做）各扰动 ±10 K
///     （10 K 比场的收敛误差大几个量级、比升温一路的温度变化小两个量级），
///     切线导度 = 热流之差 ÷ 温差之差，偏置 = 标定点热流 − 切线导度 × 标定点温差；表二加两列仿射预测（偏置按节点发热 ÷ 标定点节点发热缩放），与壳解同一状态比；
///     复现检查：先在标定点原样重解一次，与整线场比 |Δ管孔|、|Δ铜排| &gt; 0.5 W 或 |Δ节点均温| &gt; 1 K ⇒ 印「复现不上，切线一节不可用」（描述）。
///
/// ══ ⚠ 2026-09-15 Opus 5（合并，复审后改）：记录值（RecB2／RecBudget）与上面引的全部输出文件都取自 r48_G2 工作树（底板 460d3b），即 **F 合入前的配方**：
///   压接按形心整格定电位、定温（没有 ShellMesh.ClampFaceDirichlet），压接细带 ClampBandPerHFine = 3.0（3·hFine）；不计空管管腔轴向辐射（G3 默认系数 0.023 未合入）。
///   合并树里 LineRunner.Run 走压接面上定温、缺省不铺细带（F），空管计管腔辐射（G3）⇒ 甲乙丙丁的输入场都变了，J0 逐位断言**红**。
///   合并树实跑确认（2026-09-15 15:36，输出 deliverable/R48_G2_复审二_升温两节点_{B2,误差预算}_带玻璃_2026-09-15_本次开跑于2026-09-15_1536.txt；首个断言即红，J3 场对账在合并树上没走到）：
///     B2：甲 0.48368560511695335 → 0.4535682333333、乙 92.17982276720738 → 89.23620735779798、丙 0.28829265077824573 → 0.27682633330925555、
///         几何导度 0.15470705865460616 → 0.15465689792993553、几何裕度 16.37913833597184 → 16.49537981059068、第一版裕度 17.744490542627293 → 17.92851053575189、
///         复审丁峰值 0.38674976950390416 → 0.3816521032153304、复审裕度 16.855040748709104 → 16.996568003683613；
///     误差预算：甲 22.052149557030702 → 19.68071832469684、乙 144.29106823006862 → 141.110785251047、丙 0.08665032704616493 → 0.08300074897590548、
///         几何导度 0.17547705397175803 → 0.17540923962341687、几何裕度 15.245460004422208 → 15.340100041314866、第一版裕度 16.582681672849702 → 16.744387766717473、
///         复审丁峰值 0.13608645691616772 → 0.1358201145542921、复审裕度 15.703361016748605 → 15.82320391452392。
///   **没有重记**：生产链没有关掉 F 配方的入口（R48ClampRecipeTests f 不许 LineRunner 覆写开关），这些差没有单独拆出是 F、G3 还是别的合并改动造成的；
///   G2 的快门 R48G2RampClampChannelGateTests 在合并树上全过（算术本身没动的旁证，不是拆分）。重记须先把差归到有意改动上。
///   不许放宽断言：重记 RecB2／RecBudget 时逐项注明旧值 → 新值与依据（F 配方 ⑤ 与细带缺省 0 合入、G3 管腔辐射）。重记之前本探针在交接清单里标「合并树上 J0 红、未重记」。
///   同时（复审查出）：原 Writer 按上面那几个**被 LineRunner 注释引作依据的文件名**直接整份写，合并树的 deliverable 里没有这些文件 ⇒ 一跑就按合并树配方新建同名文件、冒充被引的那份。
///   Writer 已改为只写带开跑时刻的新文件（…_本次开跑于yyyy-MM-dd_HHmm.txt），不带时刻的文件名只属于 G2 路原跑。
/// </summary>
[Trait("速度", "慢")]
public class R48G2RampClampChannelTests
{
    private readonly ITestOutputHelper _out;
    public R48G2RampClampChannelTests(ITestOutputHelper o) { _out = o; }

    /// <summary>带号数：走 SizerResult.Signed（复审二 2026-09-15 Opus 5：原探针自造 Sg，审查意见 minor；Signed 同日补了「负数取整成零」那一支）</summary>
    private static string Sg(double v, int dec)
        => SizerResult.Signed(v, dec == 0 ? "+0;−0" : "+0." + new string('0', dec) + ";−0." + new string('0', dec));

    /// <summary>逐位记录：甲、乙（改前基线）；丙、第一版热稳定裕度（第一版输出）；几何导度与几何裕度（改前基线）；丁峰值与复审热稳定裕度（复审输出）</summary>
    private sealed record Rec(double V1, double V2, double V3, double StabGeom, double ClampGeom, double StabV1, double V4, double StabV2);
    // 2026-09-15 Opus 5（合并，复审后改）：下面两组记录取自 F 合入前的配方（形心整格 + 3·hFine 细带、无管腔辐射；r48_G2 底板 460d3b）—— 合并树上实跑 J0 红（新值见类注释末节），未重记。
    private static readonly Rec RecB2 = new(0.48368560511695335, 92.17982276720738, 0.28829265077824573, 16.37913833597184, 0.15470705865460616, 17.744490542627293,
                                            0.38674976950390416, 16.855040748709104);
    private static readonly Rec RecBudget = new(22.052149557030702, 144.29106823006862, 0.08665032704616493, 15.245460004422208, 0.17547705397175803, 16.582681672849702,
                                                0.13608645691616772, 15.703361016748605);

    [Fact] public void B2_带玻璃_导航网格() => RunDesign("B2", emptyTube: false, RecB2);
    [Fact] public void B2_空管_导航网格() => RunDesign("B2", emptyTube: true, null);
    [Fact] public void 误差预算_带玻璃_导航网格() => RunDesign("误差预算", emptyTube: false, RecBudget);
    [Fact] public void 误差预算_空管_导航网格() => RunDesign("误差预算", emptyTube: true, null);
    [Fact] public void 现役锚点_导航网格() => RunInService();

    /// <summary>本进程开跑时刻（2026-09-15 Opus 5（合并，复审后改））：输出只写「…_本次开跑于{它}.txt」，不再写被引作依据的原文件名。</summary>
    private static readonly string RunStamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");

    private Action<string> Writer(string fileName)
    {
        var sb = new StringBuilder();
        // 2026-09-15 Opus 5（合并，复审后改）：原为 Path.Combine(…, fileName) 整份写 —— 见类注释末节（合并树里会新建同名文件冒充被引的证据）
        string file = Path.Combine(HandoverDoc.Root(), "deliverable", Path.GetFileNameWithoutExtension(fileName) + "_本次开跑于" + RunStamp + Path.GetExtension(fileName));
        return s =>
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        };
    }

    private const string Judgments =
        "跑前写死的判读（复审二）：J0 甲乙丙三版峰值、几何导度／几何裕度／第一版裕度、复审丁峰值／复审裕度与记录逐位相同（断言，只对有记录的两跑）；J3 标定点场对账（断言，丁解出来的每一跑）；"
      + "J1 丁峰值 > 100 K ⇒ 未修好（描述）；J2′ 末段（最接近 1100 °C 的取点，不含终点）|集总法兰 − 壳解节点均温| ≤ 30 K 且管来同向 ⇒ 一致（描述）；"
      + "空管、现役锚点、轨迹终点、最热格位置、切线导度与偏置只描述，没有门槛。";

    private void RunDesign(string tag, bool emptyTube, Rec? rec)
    {
        string state = emptyTube ? "空管" : "带玻璃";
        var Say = Writer($"R48_G2_复审二_升温两节点_{tag}_{state}_2026-09-15.txt");
        Say($"R48 G2 复审二：升温两节点法兰节点逐项标定 前后对照（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　算例 {tag}　{state}稳态标定　导航网格　出处：Pt_Optimize.Tests/R48G2RampClampChannelTests.cs");
        Say(Judgments);
        Say(rec is null
            ? "记录出处：本跑没有前几版记录（复审二新加），J0 只打印不断言。"
            : "记录出处：甲乙与几何导度 deliverable/R48_G2_改前基线_2026-09-15.txt；丙与第一版裕度 deliverable/R48_G2_升温两节点补铜排通道_" + tag + "_2026-09-15.txt；"
              + "丁峰值与复审裕度 deliverable/R48_G2_复审_升温两节点逐项标定_" + tag + "_2026-09-15.txt。");

        DesignInputs p; DesignSpec d;
        if (tag == "B2")
        {
            p = new DesignInputs { SplitSharedFlangeDraw = true };
            d = DesignSpec.W08.Clone();
            d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
            d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
            d.TubeInsulMm = 7.5;
            d.DiscInsulMm = new[] { 10.5, 3.5, 5.0, 10.0 };
            d.TabInsulMm = new[] { 5.0, 3.5, 5.0, 10.5 };
            d = d.Fit(); d.SizeTongues(p);
        }
        else
        {
            p = new DesignInputs();
            d = DesignSpec.W08.Clone();
            d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
            d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
            d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
            d = d.Fit(); d.SizeTongues(p);
        }
        var lc = emptyTube
            ? d.BuildCase(p, checkRamp: false, emptyTube: true, emptyTubeSetpoint: EmptyTubeSetpoint.RampTarget)
            : d.BuildCase(p, checkRamp: false);
        if (tag == "B2") { lc.CoupleTolK = 0.25; lc.CoupleMaxRounds = Math.Max(lc.CoupleMaxRounds, 4000); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = LineRunner.Run(lc);
        Assert.True(r.Ok, "整线解不出来：" + r.Message);
        Say($"LineRunner.Run：{sw.Elapsed.TotalSeconds:0} s　耦合收敛 {r.Converged}　耦合容差 {lc.CoupleTolK} K　空管 {lc.EmptyTube}（空管控温点取法 {lc.EmptyTubeSetpointFrom}）　"
          + $"升温 {lc.RampFromC:0}→{lc.RampTargetC:0} °C、{lc.RampRateKPerH:0} K/h");
        Analyze(lc, r, rec, Say);
    }

    private void RunInService()
    {
        var Say = Writer("R48_G2_复审二_升温两节点_现役锚点_2026-09-15.txt");
        Say($"R48 G2 复审二：升温两节点 现役锚点（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　导航网格　出处：Pt_Optimize.Tests/R48G2RampClampChannelTests.cs");
        Say(Judgments);
        Say("几何与输入同 Program.cs「--flangestab」的现役对照：Ø120／舌长 200、半宽 40／厚 2.0／管壁 1.0、管纤维 2.5／圆盘保温 2.5、舌裸／舌端不接铜排（夹持温度 −1）／控温点 1150/1080/1050。");
        Say("⚠ +215 K（HANDOVER §4.2s）是「--ramp2」那条输入路径算的（图纸几何、按许用 J 定电流、1050 °C 电流场参考电阻、不解稳态热场），与本路径输入不同，不要求复现，只并列。");
        var pO = new DesignInputs();
        pO.Layer1.ThicknessMm = 2.5; pO.Layer1.Enabled = true;
        pO.WallMinMm = 1.0;
        pO.FlangeInsulThickMm = 2.5; pO.FlangeInsulated = true;
        pO.BusbarClampTempC = -1;
        FlangePlate MkO(double t) => new()
        {
            DiscRadiusMm = 60, HoleRadiusMm = 26,
            TabEndXMm = -200, TabEndHalfWidthMm = 40,
            ThicknessMm = t, ThickenedMm = t,
            InsulBoundaryXMm = double.NaN,
        };
        var lc = new LineCase
        {
            Base = pO, WallMm = 1.0, UseMeasuredCurrent = false, CheckRamp = false,
            SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
            FlangePlates = new[] { MkO(2.0), MkO(2.0), MkO(2.0), MkO(2.0) },
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LineResult r;
        try { r = LineRunner.Run(lc); }
        catch (Exception ex) { Say($"LineRunner.Run 抛异常：{ex.Message} ⇒ 现役锚点这一跑没有数（描述）"); return; }
        Say($"LineRunner.Run：{sw.Elapsed.TotalSeconds:0} s　Ok {r.Ok}　耦合收敛 {r.Converged}　{r.Message}");
        if (!r.Ok) { Say("整线解不出来 ⇒ 现役锚点这一跑没有数（描述，不是失败）"); return; }
        Analyze(lc, r, null, Say);
    }

    private sealed class Row
    {
        public double T, Tt, Tf, I; public string Tag = ""; public bool IsEnd;
        public RampTwoNode.FlangeFlows F = null!;
        public RampTwoNode.NodeCalibration Kk = null!;
        public ShellThermalResult Th = null!;
        public PlateThermalSetup Ts = null!;
        public double[] J = null!;
        public double TClamp; public int Its;
    }

    private void Analyze(LineCase lc, LineResult r, Rec? rec, Action<string> Say)
    {
        Say("── 逐片稳态场的法兰节点标定（每片都标一次，只打印；生产只用最不利片）");
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            var f = r.Flanges[j];
            var tsj = LineRunner.PlateThermalInputs(lc, j, f.CurrentA, f.Mesh?.SourceField);
            var kj = RampTwoNode.CalibrateNode(f.Mesh, f.TField, f.CellGenW, f.CellLossW, f.FieldBoundaryCell, f.QClampW, f.QFromTubeW, f.TRootC, f.CurrentA, tsj.P2, excludeBoundaryCells: true);
            Say($"   {f.Name,-8} 电流 {f.CurrentA:0.0} A　管根 {f.TRootC:0.00} °C　片最高 {f.TMaxC:0.0} °C　节点 {kj.NodeCells} 格、均温 {kj.TNodeC:0.0} °C　"
              + $"节点账 发热 {kj.QGenW:0.00}（整片 {f.QGenW:0.00}）− 表面 {kj.QLossW:0.00}（整片 {f.QLossW:0.00}）− 铜排 {kj.QClampW:0.00} + 管孔 {Sg(kj.QFromTubeW, 3)} = 残差 {Sg(kj.ResidualW, 4)} W（场 {Sg(f.EnergyResidualW, 4)}）　"
              + $"G {kj.GEffWPerK:0.0000} W/K、r {kj.FollowRatio:0.0000}、孔侧 {kj.HolePlateGWPerK:0.00000} W/K、参考电阻 {kj.ResistanceRefOhm:E5} Ω　舌保温 {tsj.TabInsulThickMm:0.0}　圆盘保温 {lc.DiscInsulEffectiveAt(j):0.0} mm　{(kj.Ok ? "" : "★ " + kj.Note)}");
        }

        var yes = LineRunner.FlangeLumped(lc, r.Flanges, excludeClampCells: true)!;
        var no = LineRunner.FlangeLumped(lc, r.Flanges, excludeClampCells: false)!;
        var fw = r.Flanges[yes.Index];
        var pl = yes.Plate;
        Assert.NotNull(yes.RampIn); Assert.NotNull(no.RampIn);
        var k = yes.Calib;
        Say("");
        Say($"── 最不利片 {fw.Name}（发热最大）：网格取自本片场 {yes.MeshFromField}　排除压接格 {yes.ClampAreaMm2:0.0} mm²　质量 {yes.RampIn!.FlangeMassG:0.00} g（不排除 {no.RampIn!.FlangeMassG:0.00} g）"
          + $"　圆盘保温面 {yes.RampIn.FlangeAreaInsulMm2:0.0} mm²（{yes.DiscInsulMm:0.0} mm）　舌片区 {yes.RampIn.FlangeAreaBareMm2:0.0} mm²（{yes.RampIn.TabInsulThickMm:0.0} mm）　单面　舌端边界 {k.Mode}");
        Say($"   标定：{k.Note}");
        Say($"   节点温度 {k.TNodeC:R} °C　参考电阻 {k.ResistanceRefOhm:R} Ω（改前 {yes.LegacyRefOhm:R} Ω 配管根 {fw.TRootC:R} °C）　"
          + $"铜排 G {k.GEffWPerK:R} W/K、r {k.FollowRatio:R}（⇒ 整片热稳定取 G·(1−r) {k.StabClampWPerK:0.0000}）　孔侧导度 {k.HolePlateGWPerK:R} W/K　"
          + $"表面系数 κ {yes.SurfaceScale:R}　标定点管节点 {yes.TubeNodeAtCalibC:0.00} °C　{(yes.Ramp is null ? "★ 丁没解：" + yes.RampError : "")}");

        RampTwoNode.Inputs V(LineRunner.FlangeLumpedOut src, bool channel, bool tab, bool gen, bool surf, bool hole)
        {
            var g = src.RampIn!.Clone();
            if (!channel) { g.ClampConductanceWPerK = 0; g.ClampFollowRatio = 0; }
            if (!tab) g.TabInsulThickMm = double.NaN;
            if (!gen) { g.FlangeResistanceRefOhm = src.LegacyRefOhm; g.FlangeRefTempC = fw.TRootC; }
            if (!surf) { g.SurfaceCalibLossW = double.NaN; g.SurfaceCalibTempC = double.NaN; }
            if (!hole) g.HolePlateConductanceWPerK = double.NaN;
            return g;
        }
        const string N1 = "甲 A 路前口径（不排除压接格、无通道、舌裸、发热配管根、表面不标定、孔侧几何式）";
        const string N2 = "乙 A 路后缺通道（排除，其余同甲）";
        const string N3 = "丙 G2 第一版（通道 + 舌保温；发热配管根、表面不标定、孔侧几何式）";
        const string N4 = "丁 复审生产（四项节点口径标定）";
        bool calibOk = yes.Ramp is not null;
        var versions = new List<(string name, DesignInputs pp, RampTwoNode.Inputs gi)>
        {
            (N1, no.RampP!, V(no, false, false, false, false, false)),
            (N2, yes.RampP!, V(yes, false, false, false, false, false)),
            ("乙1 只补舌片保温", yes.RampP!, V(yes, false, true, false, false, false)),
            ("乙2 只补铜排通道", yes.RampP!, V(yes, true, false, false, false, false)),
            (N3, yes.RampP!, V(yes, true, true, false, false, false)),
        };
        if (calibOk)
        {
            versions.Add(("丙+ 丙再把发热标定到节点口径", yes.RampP!, V(yes, true, true, true, false, false)));
            versions.Add(("丁′ 再标定表面散热（孔侧仍几何式）", yes.RampP!, V(yes, true, true, true, true, false)));
            versions.Add((N4, yes.RampP!, yes.RampIn));
        }
        var results = new Dictionary<string, RampTwoNodeResult>();
        Say("");
        Say("── 升温期「法兰 − 管」峰值与峰值时刻、升温末段的法兰节点热流（W；热容吸收 = 发热 − 表面 − 铜排 + 管来）");
        foreach (var (name, pp, gi) in versions)
        {
            var rt = RampTwoNode.Solve(pp, gi);
            results[name] = rt;
            Say($"   {name}");
            Say($"      峰值 {rt.MaxFlangeMinusTubeK:R} K　{rt.TimeAtMaxDeltaS / 3600:0.00} h　管温 {rt.TTubeAtMaxDeltaC:0.0} °C　法兰全程峰 {rt.TFlangePeakC:0.0} °C　电流 {rt.CurrentStartA:0.0}→{rt.CurrentEndA:0.0} A　到温 {rt.TubeReached}　κ {rt.SurfaceScale:0.0000}　孔侧 {rt.HolePlateConductanceWPerK:0.00000}　{rt.Note}");
            foreach (var (lbl, a) in new[] { ("峰值时刻", rt.AtPeak), ("末了时刻", rt.AtEnd) })
                Say($"      {lbl}：管 {a.TTubeC:0.0} °C　法兰 {a.TFlangeC:0.0} °C　段电流 {a.CurrentA:0.0} A　发热 {a.GenW:0.00}　表面 {a.SurfaceW:0.00}（圆盘 {a.SurfaceDiscW:0.00} + 舌 {a.SurfaceTabW:0.00}）"
                  + $"　铜排 {a.ClampW:0.00}{(double.IsNaN(a.ClampTempC) ? "" : $"（夹持 {a.ClampTempC:0} °C）")}　管来 {Sg(a.FromTubeW, 2)}　热容吸收 {Sg(a.StorageW, 3)}（C {a.CapJPerK:0.0} J/K）");
        }

        // ── J0 回归
        double v1 = results[N1].MaxFlangeMinusTubeK, v2 = results[N2].MaxFlangeMinusTubeK, v3 = results[N3].MaxFlangeMinusTubeK;
        double v4 = calibOk ? results[N4].MaxFlangeMinusTubeK : double.NaN;
        // 复审二（审查意见 minor「手抄了热稳定裕度的公式」）：第一版口径与几何口径都调生产的 StabilityCheck，只换夹持导度
        double stabV1 = LineRunner.StabilityCheck(lc, fw, yes, k.GEffWPerK).Margin;
        double stabGeomCall = LineRunner.StabilityCheck(lc, fw, yes, null).Margin;
        Say("");
        if (rec is not null)
        {
            bool j0 = v1 == rec.V1 && v2 == rec.V2 && v3 == rec.V3 && yes.Stab.DClampGeomDT == rec.ClampGeom && yes.Stab.MarginGeomClamp == rec.StabGeom
                   && stabGeomCall == rec.StabGeom && stabV1 == rec.StabV1 && v4 == rec.V4 && yes.Stab.Margin == rec.StabV2;
            Say($"J0 回归：甲 {v1:R}（记录 {rec.V1:R}）　乙 {v2:R}（记录 {rec.V2:R}）　丙 {v3:R}（记录 {rec.V3:R}）　"
              + $"几何导度 {yes.Stab.DClampGeomDT:R}（记录 {rec.ClampGeom:R}）　几何裕度 {yes.Stab.MarginGeomClamp:R}／调 StabilityCheck(不给) {stabGeomCall:R}（记录 {rec.StabGeom:R}）　第一版裕度 {stabV1:R}（记录 {rec.StabV1:R}）　"
              + $"复审丁峰值 {v4:R}（记录 {rec.V4:R}）　复审裕度 {yes.Stab.Margin:R}（记录 {rec.StabV2:R}）"
              + $" ⇒ {(j0 ? "逐位相同" : "★★ 不同 —— 算术被改动，本探针结论作废")}");
            Assert.Equal(rec.V1, v1); Assert.Equal(rec.V2, v2); Assert.Equal(rec.V3, v3);
            Assert.Equal(rec.ClampGeom, yes.Stab.DClampGeomDT); Assert.Equal(rec.StabGeom, yes.Stab.MarginGeomClamp); Assert.Equal(rec.StabGeom, stabGeomCall);
            Assert.Equal(rec.StabV1, stabV1);
            Assert.True(calibOk, "有记录的这一跑复审时丁解得出，现在解不出：" + yes.RampError);
            Assert.Equal(rec.V4, v4); Assert.Equal(rec.StabV2, yes.Stab.Margin);
        }
        else
            Say($"J0：本跑没有记录，只打印 —— 甲 {v1:R}　乙 {v2:R}　丙 {v3:R}　几何导度 {yes.Stab.DClampGeomDT:R}　几何裕度 {stabGeomCall:R}　第一版口径裕度 {stabV1:R}　丁峰值 {(calibOk ? v4.ToString("R") : "没解")}　生产裕度 {yes.Stab.Margin:R}");

        var stabRow = r.Checks.First(c => c.Name.StartsWith(LineResult.Key.FlangeStab, StringComparison.Ordinal));
        var rampRow = r.Checks.First(c => c.Name.StartsWith(LineResult.Key.RampField, StringComparison.Ordinal));
        if (yes.Stab.Undetermined) Assert.True(stabRow.Undetermined && double.IsNaN(stabRow.Actual));
        else Assert.Equal(yes.Stab.Margin, stabRow.Actual);
        Assert.True(rampRow.Undetermined && double.IsNaN(rampRow.Actual) && rampRow.Withheld, "判据表升温那一行复核前应暂不给数");
        Say($"整片热稳定（判据表参考量）：{(yes.Stab.Undetermined ? "判不了" : yes.Stab.Margin.ToString("R"))}（夹持 {yes.Stab.DClampDT:0.0000}、出处位 {yes.Stab.ClampFromField}；表面 {yes.Stab.DSurfDT:0.0000} + 管孔 {yes.Stab.DTubeDT:0.0000}（孔壁几何式），发热 {yes.Stab.DGenDT:0.0000} W/K）");
        Say("判据表附注（升温）：" + rampRow.Note);
        Say("判据表附注（热稳定）：" + stabRow.Note);

        // ── J3 场对账
        if (calibOk)
        {
            var m = new RampTwoNode.Model(yes.RampP!, yes.RampIn);
            double gen = 0, loss = 0;
            for (int c = 0; c < fw.Mesh!.CellCount; c++) if (!fw.FieldBoundaryCell[c]) { gen += fw.CellGenW[c]; loss += fw.CellLossW[c]; }
            double tt = m.TubeNodeTempAtRoot(fw.TRootC, k.TNodeC, fw.QFromTubeW);
            var f = m.Flows(0, tt, k.TNodeC, fw.CurrentA / yes.RampIn.SharedFactor);
            bool clampRel = fw.QClampW == 0 ? f.ClampW == 0 : Math.Abs(f.ClampW / fw.QClampW - 1) <= 1e-9;
            bool j3 = Math.Abs(f.GenW / gen - 1) <= 1e-9 && Math.Abs(f.SurfaceW / loss - 1) <= 1e-9 && clampRel
                   && Math.Abs(f.FromTubeW / fw.QFromTubeW - 1) <= 1e-6 && Math.Abs(f.StorageW - fw.EnergyResidualW) <= 1e-6 * gen;
            Say("");
            Say($"J3 场对账（标定点：管节点 {tt:0.000} °C、法兰 {k.TNodeC:0.000} °C、段电流 {f.CurrentA:0.0} A）：发热 {f.GenW:R} ↔ {gen:R}　表面 {f.SurfaceW:R} ↔ {loss:R}　"
              + $"铜排 {f.ClampW:R} ↔ {fw.QClampW:R}　管来 {f.FromTubeW:R} ↔ {fw.QFromTubeW:R}　热容吸收 {f.StorageW:R} ↔ 场残差 {fw.EnergyResidualW:R} ⇒ {(j3 ? "逐项对上" : "★★ 对不上")}");
            Assert.True(j3, "J3 场对账没过");
        }
        else Say($"J3：丁没解（{yes.RampError}），不适用。");

        // ── J1
        Say("");
        Say(!calibOk ? "J1：丁没解，不适用（判据表那一行暂不给数、写算不出来的原因）。"
            : v4 > 100
            ? $"J1：丁峰值 {v4:0.00} K > 100 K ⇒ **未修好**。分量见上（峰值时刻、末了时刻）与下（沿轨迹、壳解对照）。"
            : $"J1：丁峰值 {v4:0.00} K ≤ 100 K ⇒ 只说量级过了物理把关人跑前门槛；判据表那一行照样暂不给数、**待复核**（法兰温度是整片平均，不代表片上最热处；四项只在标定点上对得上）。");

        // ── 准静态壳解
        var mesh = fw.Mesh!;
        double shareF = yes.RampIn.SharedFactor;
        bool fixedT = k.Mode == ShellThermal.ClampBoundary.FixedTemp;
        bool follow = fixedT && double.IsFinite(k.FollowRatio);
        double tClampCal = fixedT ? k.TClampC : double.NaN;
        (RampTwoNode.NodeCalibration kk, ShellThermalResult th, double tClamp, int its, PlateThermalSetup ts, double[] J) Shell(double tTube, double iSeg, double? clampFixedC)
        {
            double iJ = shareF * iSeg;
            double scale = iJ / fw.CurrentA;
            var J = fw.JField.Select(x => x * scale).ToArray();
            PlateThermalSetup ts = null!;
            double tClamp = clampFixedC ?? tClampCal;
            ShellThermalResult th = null!;
            RampTwoNode.NodeCalibration kk = null!;
            int it = 0;
            for (; it < 8; it++)
            {
                ts = LineRunner.PlateThermalInputs(lc, yes.Index, iJ, mesh.SourceField);
                if (fixedT) ts.P2.BusbarClampTempC = tClamp;
                th = LineRunner.SolvePlateThermal(mesh, J, tTube, ts);
                kk = RampTwoNode.CalibrateNode(mesh, th.T, th.CellGenW, th.CellLossW, th.BoundaryCell, th.QToClampW, th.QFromTubeW, tTube, iJ, ts.P2, excludeBoundaryCells: true);
                if (clampFixedC is not null || !follow) { it++; break; }
                double tNew = k.TColdC + k.FollowRatio * (kk.TNodeC - k.TColdC);
                if (Math.Abs(tNew - tClamp) < 0.5) { it++; break; }
                tClamp = tNew;
            }
            return (kk, th, tClamp, it, ts, J);
        }

        // ── 切线导度与发热偏置（审查建议的改法，只描述）
        const double dTan = 10.0;
        double iCal = fw.CurrentA / shareF;
        double gHoleTan = double.NaN, bHole = double.NaN, gClampTan = double.NaN, bClamp = double.NaN, pCal = double.NaN;
        Say("");
        Say("── 标定点上的切线导度与发热偏置（审查建议的改法，交物理把关人定；只描述）");
        {
            var s0 = Shell(fw.TRootC, iCal, fixedT ? tClampCal : null);
            double dq = s0.kk.QFromTubeW - fw.QFromTubeW, dc = s0.kk.QClampW - k.QClampW, dn = s0.kk.TNodeC - k.TNodeC;
            bool repOk = Math.Abs(dq) <= 0.5 && Math.Abs(dc) <= 0.5 && Math.Abs(dn) <= 1.0;
            Say($"   复现：标定点原样重解 管孔 {Sg(s0.kk.QFromTubeW, 4)} W（整线场 {Sg(fw.QFromTubeW, 4)}）　铜排 {s0.kk.QClampW:0.0000} W（{k.QClampW:0.0000}）　节点均温 {s0.kk.TNodeC:0.0000} °C（{k.TNodeC:0.0000}）　节点发热 {s0.kk.QGenW:0.000} W（{k.QGenW:0.000}）"
              + $" ⇒ {(repOk ? "复现上了" : "★ 复现不上，切线一节不可用")}");
            pCal = s0.kk.QGenW;
            var hp = Shell(fw.TRootC + dTan, iCal, fixedT ? tClampCal : null);
            var hm = Shell(fw.TRootC - dTan, iCal, fixedT ? tClampCal : null);
            double dxh = (fw.TRootC + dTan - hp.kk.TNodeC) - (fw.TRootC - dTan - hm.kk.TNodeC);
            gHoleTan = (hp.kk.QFromTubeW - hm.kk.QFromTubeW) / dxh;
            bHole = s0.kk.QFromTubeW - gHoleTan * (fw.TRootC - s0.kk.TNodeC);
            Say($"   管孔：管根 ±{dTan:0} K ⇒ 管孔 {Sg(hp.kk.QFromTubeW, 3)}／{Sg(hm.kk.QFromTubeW, 3)} W、节点均温 {hp.kk.TNodeC:0.000}／{hm.kk.TNodeC:0.000} °C、铜排 {hp.kk.QClampW:0.000}／{hm.kk.QClampW:0.000} W"
              + $"　⇒ 切线导度 {gHoleTan:0.00000} W/K、偏置 {Sg(bHole, 3)} W（标定点）　对照：割线 {k.HolePlateGWPerK:0.00000} W/K、整片热稳定的孔壁几何式 {yes.Stab.DTubeDT:0.00000} W/K　"
              + $"节点均温随管根 {(hp.kk.TNodeC - hm.kk.TNodeC) / (2 * dTan):0.0000} K/K、铜排随管根 {(hp.kk.QClampW - hm.kk.QClampW) / (2 * dTan):0.00000} W/K");
            if (fixedT)
            {
                var cp = Shell(fw.TRootC, iCal, tClampCal + dTan);
                var cm = Shell(fw.TRootC, iCal, tClampCal - dTan);
                double dxc = (cp.kk.TNodeC - (tClampCal + dTan)) - (cm.kk.TNodeC - (tClampCal - dTan));
                gClampTan = (cp.kk.QClampW - cm.kk.QClampW) / dxc;
                bClamp = s0.kk.QClampW - gClampTan * (s0.kk.TNodeC - tClampCal);
                Say($"   铜排：夹持 ±{dTan:0} K ⇒ 铜排 {cp.kk.QClampW:0.000}／{cm.kk.QClampW:0.000} W、节点均温 {cp.kk.TNodeC:0.000}／{cm.kk.TNodeC:0.000} °C、管孔 {Sg(cp.kk.QFromTubeW, 3)}／{Sg(cm.kk.QFromTubeW, 3)} W"
                  + $"　⇒ 切线导度 {gClampTan:0.00000} W/K、偏置 {Sg(bClamp, 3)} W（标定点）　对照：割线 G {k.GEffWPerK:0.00000} W/K、整片热稳定取 G·(1−r) {k.StabClampWPerK:0.00000}、舌片导热截面几何式 {yes.Stab.DClampGeomDT:0.00000} W/K　"
                  + $"节点均温随夹持 {(cp.kk.TNodeC - cm.kk.TNodeC) / (2 * dTan):0.0000} K/K、管孔随夹持 {(cp.kk.QFromTubeW - cm.kk.QFromTubeW) / (2 * dTan):0.00000} W/K");
            }
            else Say($"   铜排：舌端边界 {k.Mode}，不扰动夹持温度。");
        }

        // ── 轨迹与取点
        string trajName = calibOk ? N4 : N3;
        var vt = results[trajName];
        var mt = new RampTwoNode.Model(yes.RampP!, calibOk ? yes.RampIn : V(yes, true, true, false, false, false));
        var picks = new List<(double t, double tt, double tf, double i, string tag, bool end)>();
        foreach (double want in new[] { 300.0, 500.0, 700.0, 900.0, 1100.0 })
        {
            var s = vt.Trace.OrderBy(q => Math.Abs(q.tt - want)).First();
            if (Math.Abs(s.tt - want) < 50) picks.Add((s.t, s.tt, s.tf, s.i, "", false));
        }
        picks.Add((vt.AtPeak.TimeS, vt.AtPeak.TTubeC, vt.AtPeak.TFlangeC, vt.AtPeak.CurrentA, "峰值时刻", false));
        picks.Add((vt.AtEnd.TimeS, vt.AtEnd.TTubeC, vt.AtEnd.TFlangeC, vt.AtEnd.CurrentA, "轨迹终点", true));
        var rows = new List<Row>();
        foreach (var (t, tt, tf, i, tag, end) in picks.OrderBy(q => q.tt).ThenBy(q => q.end))
        {
            var sh = Shell(tt, i, null);
            rows.Add(new Row { T = t, Tt = tt, Tf = tf, I = i, Tag = tag, IsEnd = end, F = mt.Flows(t, tt, tf, i), Kk = sh.kk, Th = sh.th, Ts = sh.ts, J = sh.J, TClamp = sh.tClamp, Its = sh.its });
        }
        Say("");
        Say($"── 准静态壳解复核 表一：状态对照（集总 = {trajName} 的方程在它的轨迹状态上{(calibOk ? "" : "；★ 丁没解，改用丙的轨迹")}；壳解 = 同一片网格、同一边界与物性，节点各项取节点格 —— 与集总同一口径；夹持按分压假设与壳解节点均温迭代自洽）");
        Say("   管温°C  段电流A  集总法兰°C  壳解均温°C  差K     集总铜排W  壳解铜排W  集总发热W  壳解发热W  集总表面W  壳解表面W  集总管来W  壳解管孔W  管来方向  壳解节点残差W  夹持°C  壳解最热°C  壳解盘峰°C  最热−管K  收敛/轮");
        foreach (var w in rows)
        {
            bool same = Math.Sign(w.F.FromTubeW) == Math.Sign(w.Kk.QFromTubeW);
            Say($"   {w.Tt,6:0.0}  {w.I,7:0.0}  {w.Tf,9:0.0}  {w.Kk.TNodeC,9:0.0}  {Sg(w.Tf - w.Kk.TNodeC, 1),7}   {w.F.ClampW,8:0.00}  {w.Kk.QClampW,8:0.00}  {w.F.GenW,8:0.00}  {w.Kk.QGenW,8:0.00}  {w.F.SurfaceW,8:0.00}  {w.Kk.QLossW,8:0.00}"
              + $"  {Sg(w.F.FromTubeW, 2),8}  {Sg(w.Kk.QFromTubeW, 2),8}  {(same ? "同向" : "★反向"),4}  {Sg(w.Kk.ResidualW, 3),10}  {w.TClamp,5:0}  {w.Th.TMaxC,8:0.0}  {w.Th.TDiscMaxC,8:0.0}  {Sg(w.Th.TMaxC - w.Tt, 1),7}  {(w.Th.Converged ? "是" : "否")}/{w.Its}{(w.Tag.Length > 0 ? "　← " + w.Tag : "")}");
        }
        Say("── 表二：方程对照（集总各项取在**壳解节点均温**上，状态相同只比方程；管来的管节点取该时刻管温）　＋ 仿射预测（切线导度 × 温差 + 偏置 × 节点发热比，描述）");
        Say("   管温°C  壳解均温°C  发热 集总/壳解W  表面 集总/壳解W  铜排 集总/壳解W  管来 集总/壳解W  集总热容吸收W  管孔 仿射预测/壳解W  铜排 仿射预测/壳解W");
        foreach (var w in rows)
        {
            var g = mt.Flows(0, w.Tt, w.Kk.TNodeC, w.I);
            double pr = w.Kk.QGenW / pCal;
            double qhPred = gHoleTan * (w.Tt - w.Kk.TNodeC) + bHole * pr;
            double qcPred = fixedT ? gClampTan * (w.Kk.TNodeC - w.TClamp) + bClamp * pr : double.NaN;
            Say($"   {w.Tt,6:0.0}  {w.Kk.TNodeC,9:0.0}  {g.GenW,8:0.00}/{w.Kk.QGenW,-8:0.00}  {g.SurfaceW,8:0.00}/{w.Kk.QLossW,-8:0.00}  {g.ClampW,8:0.00}/{w.Kk.QClampW,-8:0.00}  {Sg(g.FromTubeW, 2),8}/{Sg(w.Kk.QFromTubeW, 2),-8}  {Sg(g.StorageW, 2),8}"
              + $"      {Sg(qhPred, 2),8}/{Sg(w.Kk.QFromTubeW, 2),-8}  {(fixedT ? qcPred.ToString("0.00") : "—"),8}/{w.Kk.QClampW,-8:0.00}{(w.Tag.Length > 0 ? "　← " + w.Tag : "")}");
        }
        Say($"── 表三：壳解最热格在哪（分区规则与 ShellThermal 同一条：{rows[0].Th.DiscZoneRule}；管孔半径 {pl.HoleRadiusMm:0.0} mm、盘半径 {pl.DiscRadiusMm:0.0} mm）");
        Say("   管温°C  最热°C  x mm    z mm    r mm   分区    离管孔mm  厚mm   J A/mm²  在哪种保温下        边界格  │ 圆盘区峰°C  r mm  │ 舌片区峰°C  r mm");
        foreach (var w in rows)
        {
            int h = 0;
            for (int c = 1; c < w.Th.T.Length; c++) if (w.Th.T[c] > w.Th.T[h]) h = c;
            var cc = mesh.Centroid[h];
            double rr = Math.Sqrt(cc.X * cc.X + cc.Z * cc.Z);
            string zone = w.Ts.DiscRadiusMm > 1e-9 ? (rr > w.Ts.DiscRadiusMm ? "舌片区" : "圆盘区") : "（按 x）";
            string insul = pl.UnderDiscInsulation(cc.X, cc.Z) ? $"圆盘保温 {yes.DiscInsulMm:0.0} mm" : $"舌保温 {w.Ts.TabInsulThickMm:0.0} mm";
            bool bnd = w.Th.BoundaryCell.Length == mesh.CellCount && w.Th.BoundaryCell[h];
            Say($"   {w.Tt,6:0.0}  {w.Th.T[h],6:0.0}  {cc.X,6:0.0}  {cc.Z,6:0.0}  {rr,5:0.0}  {zone,-4}  {rr - pl.HoleRadiusMm,7:0.0}  {mesh.Thickness[h],5:0.00}  {w.J[h],7:0.00}  {insul,-16}  {(bnd ? "是" : "否"),2}    │ {w.Th.TDiscMaxC,8:0.0}  {w.Th.DiscMaxRMm,5:0.0} │ {w.Th.TTabMaxC,8:0.0}  {w.Th.TabMaxRMm,5:0.0}{(w.Tag.Length > 0 ? "　← " + w.Tag : "")}");
        }

        string Describe(IEnumerable<Row> set)
        {
            var list = set.ToList();
            var worstDT = list.OrderByDescending(w => Math.Abs(w.Tf - w.Kk.TNodeC)).First();
            var hottest = list.OrderByDescending(w => w.Th.TMaxC - w.Tt).First();
            var mismatch = list.Where(w => Math.Sign(w.F.FromTubeW) != Math.Sign(w.Kk.QFromTubeW)).Select(w => $"管温 {w.Tt:0} °C（集总 {Sg(w.F.FromTubeW, 1)} W，壳解 {Sg(w.Kk.QFromTubeW, 1)} W）").ToList();
            return $"集总法兰温度与壳解节点均温最大相差 {Math.Abs(worstDT.Tf - worstDT.Kk.TNodeC):0.0} K（管温 {worstDT.Tt:0} °C）；管来方向不一致 {mismatch.Count} 点{(mismatch.Count > 0 ? "：" + string.Join("、", mismatch) : "")}；"
                 + $"壳解最热铂比管温最多高 {Sg(hottest.Th.TMaxC - hottest.Tt, 1)} K（管温 {hottest.Tt:0} °C）";
        }
        Say($"   轨迹描述（不是判读）：不含终点（与复审同口径）—— {Describe(rows.Where(w => !w.IsEnd))}。");
        Say($"   轨迹描述（不是判读）：含终点 —— {Describe(rows)}。");
        Say("   注：壳解管孔是定温边界（管温直接加在孔边，没串管壁翅片导度），管孔流入绝对值比集总「管来」大，只看方向。");

        // ── J2′（规则同复审，一字未改；取点不含终点）
        var late = rows.Where(w => !w.IsEnd).OrderBy(w => Math.Abs(w.Tt - 1100.0)).First();
        if (fixedT)
        {
            var lateFixed = Shell(late.Tt, late.I, tClampCal);
            Say($"   升温末段（管温 {late.Tt:0.0} °C）夹持两种假设的壳解：随法兰按比例 {late.TClamp:0} °C ⇒ 节点均温 {late.Kk.TNodeC:0.0}、最热铂 {late.Th.TMaxC:0.0} °C、管孔 {Sg(late.Kk.QFromTubeW, 2)} W、铜排 {late.Kk.QClampW:0.00} W；"
              + $"写死 {lateFixed.tClamp:0} °C ⇒ 节点均温 {lateFixed.kk.TNodeC:0.0}、最热铂 {lateFixed.th.TMaxC:0.0} °C、管孔 {Sg(lateFixed.kk.QFromTubeW, 2)} W、铜排 {lateFixed.kk.QClampW:0.00} W（收敛 {lateFixed.th.Converged}）");
        }
        bool j2 = Math.Abs(late.Tf - late.Kk.TNodeC) <= 30 && Math.Sign(late.F.FromTubeW) == Math.Sign(late.Kk.QFromTubeW);
        Say("");
        Say($"J2′（{trajName}）：升温末段（管温 {late.Tt:0.0} °C）集总法兰 − 壳解节点均温 {Sg(late.Tf - late.Kk.TNodeC, 1)} K（≤ 30）　集总管来 {Sg(late.F.FromTubeW, 2)} W、壳解管孔 {Sg(late.Kk.QFromTubeW, 2)} W（同向？）"
          + (j2 ? " ⇒ 末段集总与壳解一致（仍只是整片平均的一致，不代表片上最热处；判据表那一行照样待复核）" : " ⇒ **不一致**：集总模型在危险段与壳解对不上，这条参考量要以壳解为准或改节点定义，交物理把关人"));
    }
}
