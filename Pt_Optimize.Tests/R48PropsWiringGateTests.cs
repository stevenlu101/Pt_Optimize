using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 物性接线　**电、热物性（ρ、dρ/dT、电阻温度系数、k、cp）按牌号进求解链** —— 2026-09-23，Opus 5.5
//
//  改了什么：求解链原来直读 Materials.PtResistivity／PtThermalK／PtCp／PtTcr（写死纯铂），现在一律经 PtProps.For(牌号) 取；
//  纯铂一支调的就是那几支原函数（同一运算次序）⇒ 纯铂的数一位都不许动。
//
//  九条门各管什么（每条只证它写的那件事）：
//   1 纯铂默认逐位不变（整线小算例，全量转储的 SHA-256，含文字；按操作系统各一份记录）。
//     不覆盖（整线小算例不经过这些路径）：升温核算那一支（本算例 checkRamp: false）；参考工具页 LineSolver.SizeFlanges → CoupledSolver → PlateThermal2D／PlateCurrent2D；
//     RampScreen.Evaluate／Judge；DesignScreen.DrawBudgetW／JLimitAPerMm2；DesignCurrent.ClosedFormResistanceOhm 的闭式那一支（rf 为空时才走，本算例每片都有场参照）。
//     这几处的纯铂逐位只有门 2 在访问口层证（访问口纯铂一支逐位等于原函数），
//     调用处的运算次序没有逐位门，只经审阅。
//   2 访问口：纯铂一支（与 Tanaka-ZGS-Pt）逐位等于原函数；Pt-Rh/90-10 在 1150 °C 等于差量文件印的数；源码里不读参考热导率、不碰热膨胀。
//   3 段解：选 Pt-Rh/90-10，电阻温度系数、时间常数、热衰减长度、电阻、中点温度、反算电流都跟着变，且变的比例等于物性比。
//   4 壳体电流与温度场：均匀温度下电流分布与牌号无关、发热按 ρ 比变；非均匀温度下电流分布变（σ(T) 按牌号）；逐格发热等于 ρ(T)·J²·t·A 逐位；
//     热解的管孔抽热与总发热随牌号变（不钉方向：最高温在管孔边，方向推不出来，见门里的注）。
//   5 升温准静态与两节点：金属热容比 = cp 比，管电阻比 = ρ 比，管侧翅片导度比 = √(k 比)。
//   6 整线说明：选 Pt-Rh/90-10 时结果说明末尾写明按牌号取、数据点区间与本算例越出之处（区间含升温起止温度：本算例不核升温，
//     但设计电流闭式每轮都从 25 °C 算到目标，25 °C 是真算过的）；段温真的变了。不覆盖：两条失败出口（第一轮失败、耦合中途失败）的说明（只有源码，没有算例）。
//   7 数据不全的牌号五项一起退回纯铂、说明写明缺什么（「持久强度仍按本牌号」只在有持久强度曲线时写）；参考热导率不读；材料库里没有的名字照样抛；
//     参考工具页逐行牌号那一路：片的说明（LineSolver.JointGradeNotes）带出两侧段的退回说明，SizeFlanges 真的赋给了 FlangeResult.GradeNotes（源码字面）。
//     不覆盖：SizeFlanges 端到端（每段一次耦合解约 30 s）、界面把 GradeNotes 印出来（UI 未编译）。
//   8 源码门：Core 里直读纯铂那几支的只剩名单上的例外（逐条有因）；生产 Core 调电流解与闭式电阻的地方都传了牌号。
//   9 整线只有一个牌号来源：LineCase.GradeName 与参数表不一致当场抛。
//  不覆盖：界面（参数表下拉、结果说明的显示）、命令行 Program.cs（它自己的公式仍按纯铂）、判据全体按新物性重跑（另立一轮）。
// ════════════════════════════════════════════════════════════════════════════

public class R48PropsWiringGateTests
{
    private readonly ITestOutputHelper _o;
    public R48PropsWiringGateTests(ITestOutputHelper o) => _o = o;

    /// <summary>
    /// 门 1 的记录（小写十六进制 SHA-256；全量转储含文字，两处耗时改写成占位）。**记录取自不含接线的代码**，这样才不是拿自己比自己。
    /// 例外（2026-09-23 F3 审查后补）：§0.-20 孔弧诊断与 F3 这两条 Linux 记录是在**合并树**上录的（「不含接线、含它们」的树不存在）；
    ///   它们的中性另由对拍证明 —— 孔弧诊断：剥离版探针改前／改后逐字节对拍；F3：同树设 LineCase.GateRevertFaceHeat 后 SHA 回到 F3 前记录（R48F3FaceHeatGateTests.门3）。
    /// Linux 记录史：
    ///   - 2026-09-23 Opus 5.5 首记 13aedf7ee75e1889abed6e9bf75293bd97de3c6ddf8bb9b001e178e7c93cdb45：a468063 只加门 1 剥离版，接线前跑两次逐位相同，接线后再跑仍相同。
    ///   - 2026-09-23 Fable 5.1 **重记**（变因 F6，HANDOVER §0.-19：孔边电流场改为孔面上定电位、弧面法向距、孔面只认弧面、热稳定锚点在孔圆上，
    ///     并且转储多了 F6 的公开成员 Mesh.HoleFaceDirichlet／Faces[].ArcRadiusMm／HoleArcCentroidInside／配方新字段等）：
    ///     旧值 13aedf7e… → 新值（下面的常量）。新值取自「F6 合入、接线之前」的树（ae8d110 ＋ F6 审查后终版，只加门 1 剥离版 ZZGate1Probe，转储写到文件后 sha256sum），
    ///     再在合并树（接线 ＋ F6）上跑本门与同一探针，两份转储逐字节相同 ⇒ 接线在 F6 之上对纯铂仍逐位中性。
    ///     对拍证据：deliverable/R48_F6_接线门1_F6树与合并树对拍_2026-09-23.txt。
    ///   - 2026-09-23 **再重记**（HANDOVER §0.-20，变因 = 孔弧覆盖诊断给 ShellMesh 新加的 4 个公开字段 HoleArcCoverage／HoleArcGapCount／HoleArcMaxGapMidDeg／HoleArcMaxGapMm 进了转储，
    ///     每片 4 行、3 片共 12 行；**数值场一位没动**）：旧 e9a022cf… → 新值（下面的常量）。
    ///     **本次只在合并树上重录**：「不含接线、含本诊断」的树不存在，照上一条的做法先在不含接线的树上取数不可行。证明中性的办法换成：
    ///     改前（分支头 911c788，不含本诊断）与改后用同一剥离版探针各转储一次，改后去掉这 4 个字段的 12 行后与改前 cmp 逐字节相同（SHA 回到 e9a022cf…），diff 只有这 12 行新增。
    ///     对拍证据：deliverable/R48_图纸孔弧覆盖诊断_接线门1转储对拍_2026-09-23.txt。
    ///   - 2026-09-23 RING **再重记**（变因 = 决 101 A 孔环按解析圆判料：网格配方 MeshRecipe 新加 HoleBandCircle／HoleBandCircleSwitch／HoleBandMm 三个公开成员、
    ///     MeshRecipeRule 新加 HoleBandCircle／HoleBandCircleSwitch 两项，进了转储：每片 5 行、3 片共 15 行；**数值场一位没动** —— 本算例是解析路径，包层只包栅格厚度场，按构造不经过）：
    ///     旧 fb2c3248… → 新值（下面的常量）。证明办法同上一条：改前（1816fc6 原样，git archive 另建镜像）与改后用同一剥离版探针各转储一次，
    ///     改后去掉这 15 行后与改前 cmp 逐字节相同（SHA 回到 fb2c3248…），diff 只有这 15 行新增（值：量得 true、开关 true、带宽 0）。
    ///     对拍证据：deliverable/R48_孔环判料_接线门1转储对拍_2026-09-23.txt。Windows 上的剥离树同样要带上这 5 个成员（否则每片少 5 行）。
    ///   - 2026-09-23 RING 审查后 **再重记**（变因 = 审查 M1：网格配方 MeshRecipe 新加连通分量观测量 MeshComponents／FloatingComponents／FloatingAreaMm2 三个公开成员，进了转储：
    ///     每片 3 行、3 片共 9 行（值 1／0／0）；**数值场一位没动**）：01f51c89… → 新值（下面的常量）。证明办法同上：审查前的镜像二进制（md5 f6637b13…／0c7d0834…）与审查后镜像
    ///     用同一剥离版转储各跑一次，审查后去掉这 9 行与审查前 cmp 逐字节相同（SHA 回到 01f51c89…），diff 只有这 9 行新增。
    ///     对拍证据：deliverable/R48_孔环判料_审查后_接线门1转储对拍_2026-09-23.txt。Windows 剥离树同样要带上这 3 个成员。
    ///   - 2026-09-23 **再重记**（SEG，决 97 A「段电流连续根」，变因 = 改回参数 DesignInputs.SegCurrentContinuousRoot 这个新公开成员进了转储，
    ///     「算例.Base.SegCurrentContinuousRoot = true」**1 行**；**数值场一位没动** —— 本算例按实测电流求解（UseMeasuredCurrent），不经 FindCurrent）：旧 fb2c3248… → 新值（下面的常量）。
    ///     证明：同一剥离版探针在本树上改回（false）与连续根（true）各转储一次，两份只差这 1 行（diff 1 行）；各去掉这 1 行后 SHA 都回到 fb2c3248…。
    ///     对拍证据：deliverable/R48_段电流连续根_接线门1转储对拍_2026-09-23.txt。Windows 记录仍空（见下）。
    ///   - 2026-09-23 合并 SEG 进分支时（编排会话）：上面 RING 的两条与下面 SEG 的一条都是**各自单树**上的记录；合并树的转储同时多出 RING 的 24 行与 SEG 的 1 行，
    ///     下面的常量暂取 RING 单树的值（07f96390…），**在合并树上本门为红、属已知、非回归**；本波五条规则改动合并完后按变因「五条合并」统一重录一次。
    ///   - 2026-09-23 **再重录**（变因 = F4 分区按份额（决 28 两区都算），C3：法兰分区热账把盘缘上被 r = 盘半径 切开的格按有料面积份额拆到两区，
    ///     这些格的温度两区的峰都算；LineCase 多一个只供门的公开字段 ZoneByMaterialFraction 进了转储）：旧 fb2c3248… → 新值（下面的常量）。
    ///     **本次只在本树上重录**，做法同上一条：改前（分支头 cff38c6 的源码快照）与本树「改回」（ZoneByMaterialFraction = false）用同一探针各转储一次，
    ///     去掉「算例.ZoneByMaterialFraction」一行后逐字节相同（SHA 回到 fb2c3248…）；本树生产（开）对改回的差只在三片的分区账（盘／舌发热、散热、均温）、
    ///     分区说明，与两条判据说明里转印的那句分区说明（本算例两区峰逐位不变），判据的值一位没动。对拍证据：deliverable/R48_F4_分区按份额_接线门1与整线转储对拍_2026-09-23.txt。
    ///     Windows 记录仍空（见下），填数时照上面的剥离树做法再加 F4 的这几处改动。
    ///   - 2026-09-23 **三录**（变因 = F4 份额收整，审查 F4-M1：解析板上料全在盘内的格积分舍入出份额 1 − 1e-15，原被当成切开格；
    ///     现按 ShellThermal.ZoneShareSnapTol = 1e-9 收整成 1）：ffc61757… → 新值（下面的常量）。本算例上的差只在三片分区说明里的格数（62／62／64 → 54／54／54）
    ///     与两条判据说明里转印的那句，数值一个没动；改回（关）的转储与收整前逐字节相同，去掉「算例.ZoneByMaterialFraction」一行后 SHA 仍是 fb2c3248…。
    ///     对拍证据：deliverable/R48_F4_分区按份额_证据_2026-09-23/（接线门 1 审查前／审查后的开、关转储与 SHA）。
    ///   - 2026-09-23 合并 C3 进分支时（编排会话）：上面 C3 的一条同样是单树记录；合并树的转储同时多出 RING 24 行、SEG 1 行、C3 1 行，常量仍取 RING 单树值，**合并树上本门为红、属已知、非回归**，本波合并完统一重录。
    ///   - 2026-09-23 **再重记**（变因 = F3 发热改按面，HANDOVER F3 节：热场改吃面发热等效 J，每格 ½ΣQ·ΔV，全片 = I·U；决 27）：旧 fb2c3248… → 新值（下面的常量）。
    ///     只在合并树上重录（「不含接线、含 F3」的树不存在）。证明「只有 F3 这一处在变」的办法：同一棵树上设 LineCase.GateRevertFaceHeat（F3 改回位）跑本算例，
    ///     剔掉那一句改回说明后转储 SHA = fb2c3248…（逐字节回到 F3 前，门 R48F3FaceHeatGateTests.门3 每跑都判）；开与改回两份转储各 1250 行、差 168 行，
    ///     全在热场及其下游（片的发热／抽热／温度场／峰位／局部热稳定、段端温、判据值与说明），电位（VField）、重构 J（JField）、JMax、铂重一行不差。
    ///     （F3 第一版让局部热稳定也吃发热等效 J，那一版的开 SHA 是 e10479ad…、差 179 行，只在本会话跑过，不是记录；终版局部热稳定用重构 J，见 R48F3FaceHeatGateTests.门6。）
    ///     对拍证据：deliverable/R48_F3_接线门1_开与改回转储对拍_2026-09-23.txt。
    ///   - 2026-09-23 合并 C2 进分支时（编排会话）：C2 的记录同样是单树的；合并树上本门为红、属已知、非回归，本波合并完统一重录。
    ///   - 2026-09-23 **再重记**（C4′，变因 = **决 29 自适应**：细区半径计划进了转储 —— 算例新字段 LineCase.MeshFineRadiusPlan（本算例不经计划 ⇒ 一行「= null」）、
    ///     各片网格配方新属性 MeshRecipe.FineRadiusMm（生效参数的记录，本算例 = 算例缺省 50），共 4 行；**数值场一位没动**）：旧 fb2c3248… → 新值（下面的常量）。
    ///     证明中性：同一份转储去掉这 4 行后 SHA-256 回到 fb2c3248…（逐字节相同）。对拍证据：deliverable/R48_F7自适应_接线门1转储去行对拍_2026-09-23.txt（临时探针 ZZF7Gate1DeLineProbe，跑完已删）。
    ///   - 2026-09-23 合并 C4′ 进分支时（编排会话）：C4′ 的记录同样是单树的（bc732f5d…）；合并树上本门为红、属已知、非回归，本波五条（RING／SEG／C3／C2／C4′）合并完按变因「五条合并」统一重录。
    /// Windows：**还没有记录** —— Windows 上本门红并印出本机的 SHA。填数的做法：在 F6 合入之后、接线之前的那个状态（本分支上接线提交 265ff6a 的父提交 ＋ F6 提交的 Core/ShellMesh、
    ///   ShellCurrent、ShellThermal、QuadMesher、LineRunner 五档，或直接用 git 把接线提交 revert 掉）只加门 1 的剥离版跑出那个数；**不要在合并树上记**。
    ///   「a468063 只加本档」那句旧说明不可行：本档其余 8 条门引用 PtProps，在 a468063 上编译不过。
    ///   2026-09-23（§0.-20）补：那棵剥离树还要带上孔弧覆盖诊断（Core/ShellMesh.cs 的 4 个字段与 MeasureHoleArcCoverage／ComputeHoleArcCoverage、生成器里那一行调用），
    ///   否则转储每片少 4 行、记下的数与本树对不上；诊断不动数值场（Linux 对拍见上）。
    ///   2026-09-23（SEG）补：那棵剥离树还要带上 DesignInputs.SegCurrentContinuousRoot（公开属性，缺省 true，转储 1 行「算例.Base.SegCurrentContinuousRoot = true」），
    ///   否则记下的数与本树对不上；本算例按实测电流求解、不经 FindCurrent，数值场不变（Linux 对拍见上）。也可以在剥离树上取数、与本树比对前先去掉这一行。
    ///   2026-09-23（C4′）补：还要带上 F7′ 的两个记录字段（LineCase.MeshFineRadiusPlan、MeshRecipe.FineRadiusMm），否则转储少 4 行；它们不动数值场（Linux 去行对拍见上）。
    /// </summary>
    private const string LinuxRecord = "07f9639043b10da075bd4aacac621dd20760e2d6df79f06e70ba3b6051905fef";
    // 合并 SEG 后：SEG 单树记录为 8e0ffad1e2f972256b28e34a64f9ae00efd139f6d7ffd8ca0c1fe46adc5e69b6；合并树实数待本波合并完统一重录（见头注）。
    // 合并 C3 后：C3 单树记录为 8d28e3186bb678d05b5a153461627df5474fbd08dfbfc4c521c6ab465da847df；合并树实数待本波合并完统一重录（见头注）。
    // 合并 C2 后：C2 单树记录为 d95530d5cc9b1dbe8ba036ec52826625dc54eb2f14a400710dbdba003bf04ba6；合并树实数待本波合并完统一重录（见头注）。
    // 合并 C4′ 后：C4′ 单树记录为 bc732f5d2a58850d0db597ab16981558ae1767369817f680f318915c2e23da24；合并树实数待本波合并完统一重录（见头注）。
    private const string WindowsRecord = "";

    /// <summary>电、热物性按牌号取的牌号（纯铂之外）。口径：MaterialDb.DataCompleteness 的电阻率与热导率／比热两类都算自有。</summary>
    private static readonly string[] PerGradeExpected = { "Pt-Rh/90-10", "Tanaka-ZGS-Pt", "Tanaka-ZGS-PtRh10", "Umicore-PtRh10" };

    /// <summary>整线小算例（与 R48EmptyTubeGateTests 的 Quick 同一做法：给定电流、耦合 2 轮）。</summary>
    internal static LineCase QuickCase(string? grade = null)
    {
        var p = new DesignInputs();
        if (grade is not null) p.GradeName = grade;
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 };
        d.SegLengthMm = new[] { 300.0, 300.0 };
        d = d.Fit();
        var c = d.BuildCase(p, checkRamp: false);
        c.UseMeasuredCurrent = true; c.MeasuredCurrentA = new[] { 1200.0, 1150.0 }; c.CoupleMaxRounds = 2;
        return c;
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(s))).ToLowerInvariant();

    /// <summary>转储里两处耗时（雅可比测量用时：结果成员 JacobianAmpSec 与说明里的「用时 x s」）每跑都变，改写成占位；其余逐字。</summary>
    internal static string DumpNoTiming(LineCase lc, LineResult r)   // 2026-09-23（F3）：private → internal，R48F3FaceHeatGateTests 的改回逐位门用同一份去耗时转储
    {
        string full = Regex.Replace(R48LineDumpTests.Dump(lc, r, withText: true), @"(结果\.JacobianAmpSec = )[^\n]*", "$1<耗时>");
        return Regex.Replace(full, @"用时 [0-9.]+ s", "用时 <耗时> s");
    }

    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    private static void RelEq(double exp, double act, double tol, string what)
        => Assert.True(Math.Abs(act - exp) <= tol * Math.Abs(exp), $"{what}：期望 {exp:R}，实际 {act:R}（相对差 {(act - exp) / exp:E3}，容差 {tol:E0}）");

    // ───────────────────────────── 1 ─────────────────────────────
    [Fact]
    public void 门_纯铂默认逐位不变_整线小算例()
    {
        var lc = QuickCase();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = LineRunner.Run(lc);
        sw.Stop();
        Assert.True(r.Ok, r.Message);
        string sha = Sha(DumpNoTiming(lc, r));
        bool win = OperatingSystem.IsWindows();
        string rec = win ? WindowsRecord : LinuxRecord;
        _o.WriteLine($"纯铂整线小算例：{sw.Elapsed.TotalSeconds:0.0} s　{(win ? "Windows" : "Linux")}　SHA-256 {sha}　记录 {(rec.Length == 0 ? "（无）" : rec)}");
        Assert.DoesNotContain(r.Notes, n => n.Contains("按牌号", StringComparison.Ordinal));   // 纯铂不加牌号说明
        Assert.True(rec.Length > 0,
            $"本机（{(win ? "Windows" : "Linux")}）还没有记录（本机印出 {sha} 只供核对）。F3 之后的取法见头注（2026-09-23 F3 补）：剥离树的数只填 PreF3WindowsRecord；"
            + (win ? "WindowsRecord" : "LinuxRecord") + " 要在合并树上、以 R48F3FaceHeatGateTests.门3 对上为前提取。");
        Assert.Equal(rec, sha);
    }

    // ───────────────────────────── 2 ─────────────────────────────
    [Fact]
    public void 门_访问口_纯铂一支调的就是原函数_逐位()
    {
        Assert.Equal(PtProps.PureGradeName, GradeChoices.DefaultGrade);
        Assert.Same(PtProps.Pure, PtProps.For("Pt"));
        Assert.True(PtProps.Pure.IsPure && !PtProps.Pure.IsFallback && PtProps.Pure.Note.Length == 0);

        var temps = Enumerable.Range(0, 1601).Select(i => (double)i).Concat(new[] { 0.05, 700.05, 1149.9, 1150.1, 1500.5 }).ToArray();
        foreach (var (name, props) in new[] { ("Pt", PtProps.Pure), ("Tanaka-ZGS-Pt", PtProps.For("Tanaka-ZGS-Pt")) })
        {
            int bad = 0; string first = "";
            foreach (double t in temps)
            {
                double drho = Materials.RhoRef * (Materials.AlphaFit + 2 * Materials.BetaFit * t);   // 求解链原来内联的那一式
                var pairs = new (string Q, double Exp, double Act)[]
                {
                    ("ρ", Materials.PtResistivity(t), props.Rho(t)), ("dρ/dT", drho, props.DRhoDT(t)),
                    ("电阻温度系数", Materials.PtTcr(t), props.Tcr(t)), ("k", Materials.PtThermalK(t), props.K(t)), ("cp", Materials.PtCp(t), props.Cp(t)),
                };
                foreach (var (q, e, a) in pairs)
                    if (!Same(e, a)) { bad++; if (first.Length == 0) first = $"{q} @ {t} °C：原函数 {e:R}，访问口 {a:R}"; }
            }
            _o.WriteLine($"{name}：{temps.Length} 个温度 × 5 项，与原函数不逐位相同的 {bad} 处" + (first.Length > 0 ? "，第一处 " + first : ""));
            Assert.True(bad == 0, $"{name} 的访问口与 Materials 原函数不逐位相同：{first}");
        }
        Assert.False(PtProps.For("Tanaka-ZGS-Pt").IsPure);   // 它走按牌号那一支（借纯铂的数，算术逐位相同），不是纯铂捷径

        // Pt-Rh/90-10 与纯铂在 1150 °C：等于差量文件（deliverable/R48_M_物性按牌号与纯铂差量_本次开跑于2026-09-18_143953.txt）印出的数，到它印的位数
        var rh = PtProps.For("Pt-Rh/90-10");
        Assert.Equal("4.839881E-07", rh.Rho(1150).ToString("0.000000E+00"));
        Assert.Equal("69.300", rh.K(1150).ToString("0.000"));
        Assert.Equal("160.000", rh.Cp(1150).ToString("0.000"));
        Assert.Equal("4.708026E-07", PtProps.Pure.Rho(1150).ToString("0.000000E+00"));
        Assert.Equal("83.790", PtProps.Pure.K(1150).ToString("0.000"));
        Assert.Equal("164.525", PtProps.Pure.Cp(1150).ToString("0.000"));
        // 按牌号那一支：dρ/dT 解析式对数值微分，电阻温度系数 = dρ/dT ÷ ρ
        double h = 1e-3, num = (rh.Rho(1150 + h) - rh.Rho(1150 - h)) / (2 * h);
        RelEq(num, rh.DRhoDT(1150), 1e-6, "Pt-Rh/90-10 dρ/dT 解析 vs 数值");
        RelEq(rh.DRhoDT(1150) / rh.Rho(1150), rh.Tcr(1150), 1e-15, "Pt-Rh/90-10 电阻温度系数");

        // RangeNote：纯铂 ""；90-10 从 25 °C 起 ⇒ 热导率低于数据点下限 100 °C、取端点 42.5（源自己说常温段不可用，照实写出）
        Assert.Equal("", PtProps.Pure.RangeNote(25, 1150));
        string rn = rh.RangeNote(25, 1150);
        _o.WriteLine("Pt-Rh/90-10 在 25–1150 °C 的区间说明：" + rn);
        Assert.Contains("热导率：本算例最低 25 °C 低于数据点下限 100 °C", rn);
        Assert.Contains("42.5", rn);
        Assert.DoesNotContain("比热", rn);                     // 比热数据点 25–1500 °C，25 °C 在区间内
        // 电阻率测试值疑点段：Pt-Rh/90-10 那一行 100–600 °C 疑为插补（PtResistivityData.Rows）⇒ 区间与它重叠就写出，不重叠不写
        Assert.Contains("电阻率测试值（Pt-Rh/90-10 行）在 100–600 °C 有疑点", rn);
        Assert.Contains("电阻率测试值（Pt-Rh/90-10 行）在 100–600 °C 有疑点", rh.RangeNote(300, 1300));
        Assert.Contains("电阻率测试值（Pt-Rh/90-10 行）在 100–600 °C 有疑点", rh.RangeNote(600, 1300));   // 端点相接也算重叠
        Assert.Equal("", rh.RangeNote(700, 1300));

        string src = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "PtProps.cs"));
        Assert.DoesNotContain("ThermalKAdvisory", src);
        Assert.DoesNotContain("PtThermalExpansion", src);
    }

    // ───────────────────────────── 3 ─────────────────────────────
    [Fact]
    public void 门_选牌号物性真的进链_段解()
    {
        var pPt = new DesignInputs();
        var p90 = new DesignInputs { GradeName = "Pt-Rh/90-10" };
        var a = SegmentSolver.SolveAtCurrent(pPt, 1100);
        var b = SegmentSolver.SolveAtCurrent(p90, 1100);
        Assert.True(a.Ok, a.Message); Assert.True(b.Ok, b.Message);
        Assert.False(a.EmptyTube); Assert.Equal(0.0, a.CavityRadKAWmPerK);   // 带玻璃：管腔辐射为 0 ⇒ 热衰减长度只含管壁 k

        var P = PtProps.Pure; var R = PtProps.For("Pt-Rh/90-10");
        double ts = pPt.TSetC;
        Assert.True(Same(P.Tcr(ts), a.TcrPerK)); Assert.True(Same(R.Tcr(ts), b.TcrPerK));
        RelEq(R.Cp(ts) / P.Cp(ts), b.TauMetalS / a.TauMetalS, 1e-12, "金属时间常数比 = cp 比");
        RelEq(Math.Sqrt(R.K(ts) / P.K(ts)), b.DecayLengthMm / a.DecayLengthMm, 1e-12, "热衰减长度比 = √(k 比)");
        foreach (var (res, pr, nm) in new[] { (a, P, "Pt"), (b, R, "Pt-Rh/90-10") })
        {
            double tMean = 0; foreach (double t in res.TMetal) tMean += t; tMean /= res.TMetal.Length;
            RelEq(pr.Rho(tMean), res.ResistanceOhm * (res.TubeAreaMm2 * 1e-6) / pPt.TubeLength, 1e-12, nm + " 电阻 × 截面 ÷ 长 = ρ(平均温度)");
        }
        int mid = a.TMetal.Length / 2;
        _o.WriteLine($"同电流 1100 A：中点 {a.TMetal[mid]:0.00} → {b.TMetal[mid]:0.00} °C；时间常数 {a.TauMetalS:0.0} → {b.TauMetalS:0.0} s；"
                   + $"衰减长度 {a.DecayLengthMm:0.00} → {b.DecayLengthMm:0.00} mm；电阻温度系数 {a.TcrPerK:E4} → {b.TcrPerK:E4}");
        Assert.True(b.TMetal[mid] > a.TMetal[mid], "同电流下 ρ 大、k 小的 Pt-Rh/90-10 中点应更热");
        Assert.Contains("按牌号「Pt-Rh/90-10」", b.Note);
        Assert.DoesNotContain("按牌号", a.Note);

        var sa = SegmentSolver.Solve(pPt); var sb = SegmentSolver.Solve(p90);
        Assert.True(sa.Ok, sa.Message); Assert.True(sb.Ok, sb.Message);
        _o.WriteLine($"反算电流（控温点 {ts:0} °C）：Pt {sa.CurrentA:0.0} A → Pt-Rh/90-10 {sb.CurrentA:0.0} A");
        Assert.True(sb.CurrentA < sa.CurrentA, "ρ 大 ⇒ 到同一控温点要的电流更小");
    }

    // ───────────────────────────── 4 ─────────────────────────────
    [Fact]
    public void 门_选牌号物性真的进链_壳体电流与热场()
    {
        var plate = new FlangePlate    // 与 RemovalPriorityTests 同一块板
        {
            DiscRadiusMm = 60, HoleRadiusMm = 26, TabEndXMm = -199.5, TabEndHalfWidthMm = 40,
            ThicknessMm = 2.0, ThickenedMm = 2.0, TabThicknessMm = 2.0, TabParallel = false, WeldFilletLegMm = 0,
        };
        var pPt = new DesignInputs();
        var p90 = new DesignInputs { GradeName = "Pt-Rh/90-10" };
        var P = PtProps.Pure; var R = PtProps.For("Pt-Rh/90-10");
        var m = FlangeMesher.Build(plate, 0, 2.0, 8.0, 50.0, pPt.BusbarClampLengthMm, 0, 0);
        int n = m.CellCount;

        // 均匀 1150 °C、参考温度也取 1150 ⇒ σ ≡ 1（x/x）⇒ 电流分布与牌号无关（逐位），发热按 ρ 比
        var uni = Enumerable.Repeat(1150.0, n).ToArray();
        var ca = ShellCurrent.Solve(m, 1000, 1.1e-4, 1150, uni, props: P);
        var cb = ShellCurrent.Solve(m, 1000, 1.1e-4, 1150, uni, props: R);
        Assert.True(ca.JMagAPerMm2.Zip(cb.JMagAPerMm2).All(z => Same(z.First, z.Second)), "均匀温度下电流分布应与牌号无关（逐位）");
        RelEq(R.Rho(1150) / P.Rho(1150), cb.TotalGenW / ca.TotalGenW, 1e-12, "均匀温度下发热比 = ρ 比");
        Assert.Equal("+2.80", ((cb.TotalGenW / ca.TotalGenW - 1) * 100).ToString("+0.00"));   // 差量文件 1150 °C 电阻率：+2.80 %

        // 非均匀温度场（纯铂热解给的）⇒ σ(T) 按牌号 ⇒ 电流分布变
        var th0 = ShellThermal.Solve(m, ca.JMagAPerMm2, pPt, tRootC: 1150, insulBoundaryX: double.NaN);
        var da = ShellCurrent.Solve(m, 1000, 1.1e-4, 1150, th0.T, props: P);
        var db = ShellCurrent.Solve(m, 1000, 1.1e-4, 1150, th0.T, props: R);
        int diff = da.JMagAPerMm2.Zip(db.JMagAPerMm2).Count(z => !Same(z.First, z.Second));
        _o.WriteLine($"非均匀温度场（{th0.TMinC:0}–{th0.TMaxC:0} °C）：{n} 格里电流密度不同的 {diff} 格；Jmax {da.JMaxAPerMm2:0.000} → {db.JMaxAPerMm2:0.000}");
        Assert.True(diff > 0, "非均匀温度下 σ(T) 按牌号，电流分布应该变");

        // 热解：同一份 J、同一管根温度，逐格发热 = ρ(T)·1e3·J²·t·A（与 ShellThermal 汇总那一式逐字相同）
        var ta = ShellThermal.Solve(m, ca.JMagAPerMm2, pPt, tRootC: 1150, insulBoundaryX: double.NaN);
        var tb = ShellThermal.Solve(m, ca.JMagAPerMm2, p90, tRootC: 1150, insulBoundaryX: double.NaN);
        foreach (var (th, pr, nm) in new[] { (ta, P, "Pt"), (tb, R, "Pt-Rh/90-10") })
            for (int i = 0; i < n; i++)
                Assert.True(Same(pr.Rho(th.T[i]) * 1e3 * ca.JMagAPerMm2[i] * ca.JMagAPerMm2[i] * m.Thickness[i] * m.Area[i], th.CellGenW[i]),
                    $"{nm} 第 {i} 格发热不是按本牌号 ρ(T) 算的");
        _o.WriteLine($"热解：最高 {ta.TMaxC:0.00} → {tb.TMaxC:0.00} °C；管孔抽热 {ta.QFromTubeW:0.000} → {tb.QFromTubeW:0.000} W；发热 {ta.QGenW:0.00} → {tb.QGenW:0.00} W");
        // 设计稿原想钉「最高温 90-10 > 纯铂」（推理：发热多、导走少）—— 实跑证伪：这块板最高温就在管孔边（钉在管根 1150 °C 附近），
        //   2026-09-23 Linux 镜像实测 1146.96 → 1146.88 °C（F6 之前的树）。所以不钉方向，只钉「热解真的变了」：管孔抽热与总发热都不同（F6 之前实测 617.3 → 554.5 W、293.2 → 323.5 W）。
    //   ★ 2026-09-23 合并后（F6，HANDOVER §0.-19；这块板有孔 R26，孔边口径变了）同门印出：Jmax 7.807 → 7.837（F6 前 8.290 → 8.317），最高 1149.57 → 1149.52 °C，
    //     管孔抽热 619.606 → 556.800 W，发热 293.56 → 323.85 W。「不钉方向」的理由（最高温在管孔边）在 F6 后仍成立；本门不断言这些值，两棵树上都过。
        Assert.NotEqual(ta.QFromTubeW, tb.QFromTubeW);
        Assert.NotEqual(ta.QGenW, tb.QGenW);
    }

    // ───────────────────────────── 5 ─────────────────────────────
    [Fact]
    public void 门_选牌号物性真的进链_升温两节点与准静态()
    {
        var pPt = new DesignInputs();
        var p90 = new DesignInputs { GradeName = "Pt-Rh/90-10" };
        var P = PtProps.Pure; var R = PtProps.For("Pt-Rh/90-10");
        const double t = 900;
        var qa = RampTwoNode.QuasiStaticBreakdown(pPt, 1.0, t, 50);
        var qb = RampTwoNode.QuasiStaticBreakdown(p90, 1.0, t, 50);
        RelEq(R.Cp(t) / P.Cp(t), qb.CapMetalJPerK / qa.CapMetalJPerK, 1e-12, "准静态金属热容比 = cp 比（密度仍纯铂）");
        RelEq(R.Rho(t) / P.Rho(t), qb.RTubeOhm / qa.RTubeOhm, 1e-12, "准静态管电阻比 = ρ 比");
        Assert.Equal(qa.LossW, qb.LossW);                                   // 散热与牌号无关

        // 两节点模型（输入同 R48G2RampClampChannelGateTests 的合成算例 Synthetic(1000, 0.6, 5.0)）
        RampTwoNode.Inputs In() => new()
        {
            WallMm = 1.0, FlangeMassG = 340, FlangeAreaInsulMm2 = 730, FlangeAreaBareMm2 = 4580,
            FlangeResistanceRefOhm = 1.7e-4, FlangeRefTempC = 1090, HoleRadiusMm = 26, PlateEqOuterRadiusMm = 45, FlangeThickMm = 1.2,
            SharedFactor = 1.0, DesignCurrentA = 1000, FromC = 25, TargetC = 1700, MaxHours = 40,
            Mode = RampControl.ConstantCurrent, TabInsulThickMm = 5.0, ClampConductanceWPerK = 0.6, ClampFollowRatio = 0.45, ClampColdEndC = 25,
        };
        var ma = new RampTwoNode.Model(pPt, In());
        var mb = new RampTwoNode.Model(p90, In());
        RelEq(R.Rho(t) / P.Rho(t), mb.RTube(t) / ma.RTube(t), 1e-12, "两节点管电阻比 = ρ 比");
        RelEq(Math.Sqrt(R.K(t) / P.K(t)), mb.GTubeFin(t) / ma.GTubeFin(t), 1e-12, "两节点管侧翅片导度比 = √(k 比)");
        _o.WriteLine($"{t:0} °C：金属热容 {qa.CapMetalJPerK:0.00} → {qb.CapMetalJPerK:0.00} J/K；管电阻 {qa.RTubeOhm:E4} → {qb.RTubeOhm:E4} Ω；"
                   + $"翅片导度 {ma.GTubeFin(t):0.0000} → {mb.GTubeFin(t):0.0000} W/K");
    }

    // ───────────────────────────── 6 ─────────────────────────────
    [Fact]
    public void 门_选牌号物性真的进链_整线说明()
    {
        var lc = QuickCase("Pt-Rh/90-10");
        var r = LineRunner.Run(lc);
        Assert.True(r.Ok, r.Message);
        string last = r.Notes[^1];
        _o.WriteLine("末条说明：" + last);
        Assert.StartsWith("★ 电阻率、电阻温度系数、热导率、比热按牌号「Pt-Rh/90-10」", last);
        Assert.Contains("热导率数据点 100–1400 °C", last);
        Assert.Contains("密度、熔点按纯铂", last);
        Assert.Equal(1, r.Notes.Count(n => n.Contains("按牌号「Pt-Rh/90-10」", StringComparison.Ordinal)));
        // 区间那一段 = 访问口对本算例温度区间给的（取法同 LineRunner.AddGradeNote：升温起止温度 + 段金属温度 + 解出来的片的最低／最高温）。
        // 升温起止温度不论 CheckRamp 都并入：本算例 checkRamp: false，但设计电流闭式（DesignCurrent.Compute）每轮都从 RampFromC（25 °C）算到目标，一路按牌号读 ρ、cp、k。
        var temps = new[] { lc.RampFromC, lc.RampTargetC }
            .Concat(r.Segments.SelectMany(s => s.TMetal)).Concat(r.Flanges.Where(f => f.TMaxC > 0).SelectMany(f => new[] { f.TMinC, f.TMaxC })).ToArray();
        string range = PtProps.For("Pt-Rh/90-10").RangeNote(temps.Min(), temps.Max());
        _o.WriteLine($"本算例温度区间 {temps.Min():0.0}–{temps.Max():0.0} °C；区间说明：{(range.Length == 0 ? "（都在数据点内）" : range)}");
        Assert.EndsWith(range.Length > 0 ? "；" + range : "密度、熔点按纯铂", last);
        Assert.False(lc.CheckRamp);
        Assert.Contains("热导率：本算例最低 25 °C 低于数据点下限 100 °C", last);   // 不核升温也写 25 °C 那一端（设计电流闭式算过它）

        var rp = LineRunner.Run(QuickCase());
        Assert.True(rp.Ok, rp.Message);
        _o.WriteLine($"段 0 中点：Pt {rp.Segments[0].TMetal[rp.Segments[0].TMetal.Length / 2]:0.00} °C → Pt-Rh/90-10 {r.Segments[0].TMetal[r.Segments[0].TMetal.Length / 2]:0.00} °C");
        Assert.False(rp.Segments[0].TMetal.SequenceEqual(r.Segments[0].TMetal), "选了 Pt-Rh/90-10，整线段温一位都没变 —— 没接进去");
    }

    // ───────────────────────────── 7 ─────────────────────────────
    [Fact]
    public void 门_数据不全的牌号一起退回纯铂_写明()
    {
        var perGrade = MaterialDb.All.Keys.Where(k => k != "Pt" && !PtProps.For(k).IsPure).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        _o.WriteLine("按牌号取的：" + string.Join("、", perGrade));
        Assert.Equal(PerGradeExpected.OrderBy(k => k, StringComparer.Ordinal), perGrade);
        foreach (var k in MaterialDb.All.Keys.Where(k => k != "Pt" && !perGrade.Contains(k)))
        {
            Assert.True(PtProps.For(k).IsFallback, k + " 应退回纯铂并标出");
            // 「持久强度仍按本牌号」只在本牌号真有持久强度曲线时写
            Assert.Equal(MaterialDb.Get(k).HasCreep, PtProps.For(k).Note.Contains("持久强度仍按本牌号", StringComparison.Ordinal));
        }

        var f = PtProps.For("FKS16/Pt");
        _o.WriteLine("FKS16/Pt：" + f.Note);
        Assert.True(f.IsFallback && f.IsPure);
        Assert.Contains("一起按纯铂算", f.Note);
        Assert.Contains("热导率与比热：无数据", f.Note);
        foreach (double t in new[] { 20.0, 700.0, 1150.0, 1400.0 })
            Assert.True(Same(Materials.PtResistivity(t), f.Rho(t)) && Same(Materials.PtTcr(t), f.Tcr(t))
                     && Same(Materials.PtThermalK(t), f.K(t)) && Same(Materials.PtCp(t), f.Cp(t)), $"FKS16/Pt 退回纯铂在 {t} °C 不逐位");
        // Pt-Rh/80-20 只有推算参考热导率 ⇒ 不读它，退回纯铂
        Assert.True(Same(Materials.PtThermalK(1150), PtProps.For("Pt-Rh/80-20").K(1150)));
        Assert.NotEqual(MaterialDb.Get("Pt-Rh/80-20").ThermalKAdvisory!.Value(1150), PtProps.For("Pt-Rh/80-20").K(1150));

        // 段解：FKS16/Pt 的数与纯铂逐位相同，说明里写明退回
        var sPt = SegmentSolver.Solve(new DesignInputs());
        var sF = SegmentSolver.Solve(new DesignInputs { GradeName = "FKS16/Pt" });
        Assert.True(sPt.Ok, sPt.Message); Assert.True(sF.Ok, sF.Message);
        Assert.Equal(R48EmptyTubeGateTests.Fingerprint(sPt), R48EmptyTubeGateTests.Fingerprint(sF));
        Assert.Contains("一起按纯铂算", sF.Note);

        // 逐行牌号那一路（参考工具页 SizeFlanges → CoupledSolver，p.GradeName = 该行牌号）：退回纯铂的说明随片带出（FlangeResult.GradeNotes）；纯铂行不写、没有的名字不抛
        var rows = new List<Segment> { new() { Name = "S1", GradeName = "Pt" }, new() { Name = "S2", GradeName = "FKS16/Pt" }, new() { Name = "S3", GradeName = "没有这个牌号" } };
        Assert.Empty(LineSolver.JointGradeNotes(rows, 0));
        Assert.Equal(new[] { "S2：" + f.Note }, LineSolver.JointGradeNotes(rows, 1));
        Assert.Equal(new[] { "S2：" + f.Note }, LineSolver.JointGradeNotes(rows, 2));
        Assert.Empty(LineSolver.JointGradeNotes(rows, 3));
        Assert.Contains("GradeNotes = JointGradeNotes(segs, j)", File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "LineSolver.cs")));

        var ex = Assert.Throws<KeyNotFoundException>(() => PtProps.For("PtRh10"));
        Assert.Contains("材料库里没有牌号", ex.Message);
    }

    // ───────────────────────────── 8 ─────────────────────────────
    [Fact]
    public void 源码门_求解链不再直读纯铂函数_例外逐条有因()
    {
        string core = Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core");
        string[] pats = { "Materials.PtResistivity(", "Materials.PtThermalK(", "Materials.PtCp(", "Materials.PtTcr(", "Materials.RhoRef *", "Materials.BetaFit" };
        // 例外（逐条有因）：
        //   PtProps.cs —— 访问口本身，纯铂一支在这里调原函数（每支 1 处；dρ/dT 那一行同时有 RhoRef * 与 BetaFit）；
        //   DesignScreen.cs 电阻率 2 处 —— ShapeFactors.ResistanceOhm 全仓无调用点；Extract 里 ρ 在形状因子里约掉、电流解等温（σ ≡ 1），与牌号无关；
        //   RemovalPriority.cs 热导率 1 处 —— 整片一个常数，只标定瓦数，去料排序与挖孔位置对它不变。
        var allowed = new Dictionary<(string, string), int>
        {
            [("PtProps.cs", "Materials.PtResistivity(")] = 1, [("PtProps.cs", "Materials.PtThermalK(")] = 1, [("PtProps.cs", "Materials.PtCp(")] = 1,
            [("PtProps.cs", "Materials.PtTcr(")] = 1, [("PtProps.cs", "Materials.RhoRef *")] = 1, [("PtProps.cs", "Materials.BetaFit")] = 1,
            [("DesignScreen.cs", "Materials.PtResistivity(")] = 2,
            [("RemovalPriority.cs", "Materials.PtThermalK(")] = 1,
        };
        var bad = new List<string>();
        foreach (string f in Directory.GetFiles(core, "*.cs"))
        {
            string name = Path.GetFileName(f);
            if (name == "Materials.cs") continue;   // 定义处
            string s = File.ReadAllText(f);
            foreach (string pat in pats)
            {
                int cnt = s.Split(pat).Length - 1;
                int want = allowed.TryGetValue((name, pat), out int w) ? w : 0;
                if (cnt != want) bad.Add($"{name} 里 {pat} {cnt} 处（应 {want}）");
            }
        }
        Assert.True(bad.Count == 0, "求解链直读纯铂函数的地方与例外名单不符：" + string.Join("；", bad));

        // 生产 Core 调电流解与闭式电阻的地方都传了牌号（取到分号为止的调用文本里有 props／PtProps.）；例外：DesignScreen 的 ShellCurrent.Solve（见上）
        var missing = new List<string>();
        int seen = 0;
        foreach (string f in Directory.GetFiles(core, "*.cs"))
        {
            string name = Path.GetFileName(f), s = File.ReadAllText(f);
            foreach (string call in new[] { "ShellCurrent.Solve(", "PlateCurrent2D.Solve(", "ClosedFormResistanceOhm(" })
            {
                for (int i = s.IndexOf(call, StringComparison.Ordinal); i >= 0; i = s.IndexOf(call, i + call.Length, StringComparison.Ordinal))
                {
                    int lineStart = s.LastIndexOf('\n', i) + 1;
                    string head = s.Substring(lineStart, i - lineStart);
                    if (head.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;       // 注释
                    if (head.Contains("public static", StringComparison.Ordinal)) continue;          // 声明
                    int end = s.IndexOf(';', i);
                    string text = s.Substring(i, end - i);
                    seen++;
                    if (name == "DesignScreen.cs" && call == "ShellCurrent.Solve(") continue;
                    if (!Regex.IsMatch(text, @"\bprops\b|PtProps\.")) missing.Add($"{name}：{text.Replace('\n', ' ')}");
                }
            }
        }
        _o.WriteLine($"Core 里电流解／闭式电阻调用 {seen} 处");
        Assert.True(seen >= 4, "一处调用都没找到 —— 门空转");
        Assert.True(missing.Count == 0, "生产 Core 调用没传牌号：" + string.Join("；", missing));
        string shell = File.ReadAllText(Path.Combine(core, "ShellCurrent.cs"));
        Assert.Contains("props: c?.Base is null ? null : PtProps.For(c)", shell);   // SolveFor（整线唯一入口）按算例牌号
    }

    // ───────────────────────────── 9 ─────────────────────────────
    [Fact]
    public void 门_整线牌号只有一个来源()
    {
        // 不一致 ⇒ Run 第一步 Normalize 当场抛（在任何求解之前）
        var c = new LineCase { Base = new DesignInputs { GradeName = "Pt" }, GradeName = "Pt-Rh/90-10" };
        var ex = Assert.Throws<InvalidOperationException>(() => LineRunner.Run(c));
        Assert.Contains("只认一个牌号来源", ex.Message);
        Assert.Throws<InvalidOperationException>(() => LineRunner.BaseSegParams(c, 0, 1150));   // 其他先 Normalize 的公开入口同一口径

        // 空 ⇒ 接参数表；段参数与法兰那一路（读 Base）拿到同一个牌号、同一个取值口
        var p = new DesignInputs { GradeName = "Pt-Rh/90-10" };
        var lc = new LineCase { Base = p, SetpointC = new[] { 1150.0 }, HeadM = new[] { 0.3 } };
        Assert.Same(PtProps.For(p), PtProps.For(lc));
        var sp = LineRunner.BaseSegParams(lc, 0, 1150);
        Assert.Equal("Pt-Rh/90-10", lc.GradeName);
        Assert.Equal("Pt-Rh/90-10", sp.GradeName);
        Assert.Same(PtProps.For(p), PtProps.For(sp));
    }
}
