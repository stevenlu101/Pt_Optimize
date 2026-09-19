using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 E 保温搜索求解器·小规模实跑（2026-09-15 Opus 5）：**在管壁 0.8 落点几何上，保温搜索求解器第一次整条链跑通。**
///
/// ══ 设置（跑前定下）
///   几何：DesignSpec.W08，板厚 0.73/1.26/1.26/0.73（管壁 0.8 落点），环倍率全 1；每层由求解器重算舌片厚与板厚下角。
///   工艺：DesignInputs 默认，共用片抽热两侧各半（SplitSharedFlangeDraw = true）。
///   管保温 15/16/17 层 = 7.5/8.0/8.5 mm；圆盘、舌各 0～24 层 = 0～12 mm（⚠ 上界是为省时定的小规模，无出处）。
///   内层单片 h = 1.0 mm（粗筛）；整线工作点用导航网格（省时 —— 两张网格不同，报告逐片印网格偏差）；耦合容差 0.25 K；
///   空管控温点取全线升温目标（BuildCase 默认，待用户定）；两端起点全 0 层与全上界；不动点最多 8 轮；三层并行、两起点并行。
///   输出：deliverable\R48_保温搜索_小规模_2026-09-15.txt（进度逐行写，最后附按层排好的完整报告）。
///
/// ══ 跑前写死的判读（2026-09-15 Opus 5，跑之前写，不许跑完改）
///   J1 与起点无关：某层两端起点终点不同（或任一端周期／没收敛／整线解不出）⇒ 该层报「结果依赖起点」或「与起点无关证不出」，**不许宣称可行**。
///   J2 配方自检：每层第一次整线解上，内层路径重解抽热与整线逐位相同；不同 ⇒ 求解器抛异常，本跑作废。
///   J3 无可行：三层都没有「与起点无关且每片两态可行」⇒ 报缺口最小的片与判据（按归一最小裕度从大到小列出所有片的缺口），不报「无解」，
///      并写明：上界 12 mm 无出处、整线用的是导航网格、γ 是两角割线（非线性与邻片串扰在报告里照印）。
///   J4 有可行：可行层按合计铂重比，建议取最轻的；**只在导航网格工作点 + h=1.0 粗筛上成立**，交付前要在 0.5 mm 上复算（本跑不做）。
///   J5 γ 可信度：三种割线相差超过 20 % 的片在报告里标出；有标出的层，结论加一句「闭合误差可能改变别的格点的排序，终点本身的判据值不受影响」。
///
/// ══ 小规模跑后（2026-09-15 Opus 5，出处 deliverable\R48_保温搜索_小规模_2026-09-15.txt）：三层都「与起点无关、不可行」，J2 三层逐位相同，J5 无标出。
///   管保温 7.5 mm 终点 圆盘 10.5/7.5/7.5/11.0、舌 7.0/3.0/3.5/9.0 mm；出口片带玻璃冷侧 +49.1 K、空管热侧 +47.8 K 两头同时越限 ——
///   看上去是两态把保温往相反方向拉。但报告里只有「两态合起来」的可行集大小，分不清「两态各自有窗口但不相交」与「某一态单独就没有窗口」，
///   而空管控温点（全线升温目标 1150 °C）让出口片空管电流 1070 A、带玻璃 879 A。于是补两个对照（下面两个 Fact，跑前写死判读）：
///
/// ══ 对照甲：管保温 7.5 mm，同设置，只多印单态可行集大小（求解器 2026-09-15 补 PlateChoice.FeasibleByState）
///   输出 deliverable\R48_保温搜索_管7点5_单态可行集_2026-09-15.txt。
///   K0 复现：终点必须与小规模那一跑的 7.5 mm 终点逐位相同（层号 圆盘 21/15/15/22、舌 14/6/7/18；出处同上）—— 不同 ⇒ 计算不确定或补的计数改了选择，本对照作废（断言）。
///   K1 逐片判读（终点那一轮）：两态可行 0 而两个单态可行都 &gt; 0 ⇒「两态窗口不相交：只动圆盘／舌保温治不了，要动几何、管保温分布或工况口径」；
///      某一单态可行 = 0 ⇒「该态单独就没有保温窗口」。
///
/// ══ 对照乙：管保温 7.5 mm，空管控温点改为沿用生产设定（EmptyTubeSetpoint.AsGiven，1150/1080/1050），其余同甲
///   输出 deliverable\R48_保温搜索_管7点5_空管沿用生产设定_2026-09-15.txt。
///   L1 若与起点无关且每片两态可行 ⇒「空管控温点取哪种口径决定了保温能不能做出可行设计 —— 必须请用户定」；
///   L2 若仍不可行，但有片的归一最小裕度比甲高出 1 以上（= 5 K 限值上缓了 5 K 以上）⇒「口径显著影响缺口，仍须用户定，但单靠它不够」；
///   L3 否则 ⇒「空管控温点口径不是主因」。每片按两跑终点的归一最小裕度逐片对照打印。
///
/// ══ 两个对照跑后对账（2026-09-15 Opus 5；判读原样按上面写死的执行，这里只记结果）
///   甲（deliverable\R48_保温搜索_管7点5_单态可行集_2026-09-15.txt，22.7 分）：K0 逐位相同。K1：片0、片3 带玻璃单态与空管单态可行都是 0（两态各自单独都没有保温窗口）；
///     片1 单态 3／2、片2 单态 3／2，两态不相交。⚠ 单态计数是在两态折中选择的工作点上、按线性闭合数的，不是「只优化一态」的不动点，只作指向。
///   乙（deliverable\R48_保温搜索_管7点5_空管沿用生产设定_2026-09-15.txt，29.9 分）：与起点无关、不可行；逐片归一最小裕度 −0.468／−2.421／−5.618／−8.102（甲 −0.585／−2.419／−6.120／−8.820）⇒ L3。
///     出口片：带玻璃 抽热 +17.34 W、管根低于基准 +45.5 K；空管（控温点 1050）抽热 −6.02 W、最热处高出基准 +44.6 K；接头电流 879／1007 A。
///
/// ════════════════════════════════════════════════════════════════════════════════════════
/// ══ ⚠ 以上三跑（小规模、甲、乙）是审查修改**之前**的配方（2026-09-15 Opus 5 补记）：整线导航网格细区半径停在 50 mm（内层 59 mm，两张网格不同族）、
///   γ 锚在全 0 层与全上界两个角、闭合没核对、裕度量化 0.01。它们的数（含上面的记录值、缺口、铂重）只属于旧配方，**不与第二版混用**，文件原样留着。
///
/// ══ 第二版小规模（审查修改后，2026-09-15 Opus 5）
///   改了什么（全部在 InsulationSearch 类注释里有依据）：
///   ① 整线导航档与内层网格都调 Solver.ApplyCaseMesh，细区半径统一 59 mm（两张网格只差 h）；网格偏差折开尔文逐片印。
///   ② γ = 当前选择的管侧响应：整线收敛态上本片抽热 ±2 W、只解段、段间端温牛顿弦法解到 0.25 K（两种解法同一不动点见 R48TubeResponseNewtonGateTests），两端各自一条割线。
///   ③ 每条整线解都做配方自检（内层同一个 SolvePoint，在非均匀选择上逐位）与「管侧单解逐位复现整线管根」自检。
///   ④ 闭合核对：上一轮闭合对本轮选择的预测 vs 本轮实值，界限 max(1 K／0.4 W, 20 %) 标 ⚠。
///   ⑤ 裕度量化步长 0.01 → 0.05（= 耦合容差 0.25 K ÷ 限值 5 K，待用户确认）；选中格点到顶逐片标出；选中格点判不了时本片「无闭合」退路。
///   设置其余与第一版相同：W08、板厚 0.73/1.26/1.26/0.73、环倍率 1、各半、管保温 15/16/17 层、圆盘舌 0～24 层、内层 h=1.0、整线导航档、耦合 0.25 K、
///   空管控温点全线升温目标、两端起点、最多 8 轮、三层并行。输出 deliverable\R48_保温搜索_小规模_第二版_2026-09-15.txt。
///
/// ══ 第二版跑前写死的判读（2026-09-15 Opus 5，跑之前写，不许跑完改）
///   J0 本跑有效：每层两条链的末轮整线与管侧响应都解出（RoundRecord.LineOk）；有一条不是 ⇒ 印「本跑作废」并断言失败（周期／轮数用完不算作废，那是 J1 的判不了）。
///   J1 与起点无关：同第一版 —— 两端终点不同 ⇒「结果依赖起点」；任一端周期／没收敛 ⇒「与起点无关证不出」；两者都不许宣称可行。
///   J2 自检：配方自检、管侧复现自检在每条整线解上逐位相同（不同时求解器抛异常，本跑作废）；这里再数一遍条数印出。
///   J3 无可行：三层都没有「与起点无关且每片两态可行」⇒ 按归一最小裕度从大到小列出所有片的缺口，每片带上终点那轮的网格偏差（折开尔文）；
///      并写明：上界 12 mm 无出处、整线用导航档、终点格点判据值带网格偏差与耦合容差、「最近格点」带闭合误差。
///   J4 有可行：同第一版（只在导航档工作点 + h=1.0 粗筛上成立，交付前要在 0.5 mm 上复算，本跑不做）。
///   J5 闭合：每层每链印闭合核对汇总；有 ⚠ 行 ⇒ 结论加一句「闭合误差可能改变别的格点的排序（终点格点自己的判据值不经闭合）」，并印收敛前最后一次有预测修正的比较里管根误差最大的行。
///   J6 与第一版对照：只印两版 7.5 mm 终点（第一版 圆盘 21/15/15/22、舌 14/6/7/18 层）是否相同；不同 ⇒ 写「配方改了（网格半径、γ、量化步长），不是重跑噪声」，不做别的推断。
///   J7 网格偏差：印每层终点（或末轮）|网格偏差折开尔文| 的最大值；&gt; 2 K 的层写「缺口数带这一截不确定度」。
///
/// ══ 第二版跑后（2026-09-15 Opus 5，deliverable\R48_保温搜索_小规模_第二版_2026-09-15.txt，37.4 分；判读原样按上面执行，这里只记结果）
///   J0 有效。J2 三层共 20 条整线解，配方自检与管侧复现全部逐位相同。
///   J1：7.5 mm、8.5 mm 与起点无关；**8.0 mm 结果依赖起点**（两条链各自收敛，终点只差片2 圆盘 9.0 对 9.5 mm，各自选中裕度 −6.189 对 −6.176，差 0.013 小于量化步长 0.05；是哪一格在两个工作点上跨了量化格没有逐格打印，机理未拆）。三层都不可行。
///   J6：7.5 mm 终点与第一版逐位相同（圆盘 21/15/15/22、舌 14/6/7/18 层）；缺口数也与第一版到千分位相同（片0 空管冷侧 +7.922 K，第一版 +7.923 K）。
///     ⇒ 这块几何上细区半径 50 → 59 mm 几乎不动整线（全 0 层带玻璃片0 抽热 67.47696585513822 → 67.47668973036566 W）；γ 改法与量化步长也没改终点。
///   J5：六条链都有 ⚠ 行。管根闭合误差按比较次序：第 1→2 轮 23～83 K（≤ 41 %，从角上出发、修正 157～205 K 的非线性区）；第 2→3 轮 0.86～2.25 K；
///     第 3→4 轮（终点附近）0.06～0.31 K。误差同号居多（实际比预测暖），与审查意见量出的旧配方现象一致。
///   J7：终点（或末轮）|网格偏差折开尔文| 最大 1.27／1.31／1.27 K；全部轮次抽热差 +0.408～+1.744 W、折 0.49～2.50 K。
///   J3 缺口（最小的在前）：片0 −0.584（7.5 mm，空管冷侧 +7.922 K）…… 片3 −8.820～−9.333（带玻璃冷侧 +49.1～+51.7 K 或空管热侧 +50.3 K）；8.5 mm 片3 圆盘到顶。
///   第一版的两个对照（甲、乙）是旧配方，本版没重跑，Fact 已删（它们的记录值属于旧配方，文件原样留着）。
///
/// ══ ⚠ 2026-09-15 Opus 5（合并）：以上**两版**的全部记录（含第二版的 J0～J7 结果、缺口、铂重、SmallRunV1T15Final）都取自 r48_E 工作树（底板 f206e70），
///   即 F 合入前的配方：压接按形心整格、3·hFine 自相似细带；散热表上限「设定 + 200 K、60 节点」（合并树已改为铂熔点上限）；
///   不计空管管腔轴向辐射（G3 默认系数 0.023）。合并树整线走压接面上定温、缺省不铺细带、铂熔点散热表、空管计管腔辐射 ⇒ 这些数不属于合并树的配方，不许与合并树的结果混用。
///   本探针（E 最终版）只断言 J0「本跑有效」与层数，不对记录值做逐位断言；输出文件同名已存在时拒跑（2026-09-15 Opus 5 I 路改：原文件在合并树里已存在 ⇒ 本探针必抛；现只写带开跑时刻的新文件，见 RunSearch）。合并后**未重跑**。
/// </summary>
[Trait("速度", "慢")]
public class R48InsulationSearchProbeTests
{
    private readonly ITestOutputHelper _out;
    public R48InsulationSearchProbeTests(ITestOutputHelper o) { _out = o; }

    /// <summary>第一版小规模那一跑 7.5 mm 层的终点（旧配方的记录值；出处 deliverable\R48_保温搜索_小规模_2026-09-15.txt「管保温 15 层」两条链第 4 轮）。只给 J6 对照印，不参与任何判定。</summary>
    private static readonly InsulationSearch.Selection SmallRunV1T15Final = new(new[] { 21, 15, 15, 22 }, new[] { 14, 6, 7, 18 });

    private (InsulationSearch.Report Rep, Action<string> Say) RunSearch(string fileName, int[] tubeLayers, EmptyTubeSetpoint emptySp, string title)
    {
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）；原为「同名已存在就抛」，合并树里原文件已在 ⇒ 本探针必抛
        string file = DeliverableOut.Stamped(fileName);
        if (File.Exists(file)) throw new InvalidOperationException($"{Path.GetFileName(file)} 已存在（同一秒开跑两次？）—— 不覆盖");
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        object fl = new();
        void Say(string s)
        {
            lock (fl)
            {
                string line = $"[{sw.Elapsed.TotalMinutes,6:0.0} 分] {s}";
                _out.WriteLine(line); sb.AppendLine(line);
                try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
            }
        }
        ThreadPool.GetMinThreads(out int wMin, out int ioMin);
        ThreadPool.SetMinThreads(Math.Max(wMin, 64), ioMin);

        var p = new DesignInputs { SplitSharedFlangeDraw = true };
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        var o = new InsulationSearch.Options
        {
            TubeLayers = tubeLayers,
            DiscLayerMax = 24, TabLayerMax = 24,
            InnerMeshMm = 1.0, WholeLineMeshMm = 0,
            CoupleTolK = 0.25, CoupleMaxRounds = 4000,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2),
            LayersInParallel = true, StartsInParallel = true,
            MaxFixedPointRounds = 8,
            EmptyTubeSetpoint = emptySp,
            Log = Say,
        };
        Say($"{title}（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　出处：Pt_Optimize.Tests/R48InsulationSearchProbeTests.cs　逻辑核 {Environment.ProcessorCount}");
        var rep = InsulationSearch.Run(d, p, o);
        // 2026-09-16 Opus 5（I 路审查意见 2，造门在下游）：每层 RunLayer 真用的板厚／舌片厚必须逐位等于 LayerDesign(d, p, 层) 的（快门 R48InsulationSearchPlateFloorTests 门 c 只在第一轮前核报告行，这里核跑完后的记录值）。
        //   ⚠ 本断言 2026-09-16 只编译、未实跑（本路不重跑保温搜索，主会话裁定 6）。
        foreach (var lr in rep.Layers.Where(l => l.Starts is not null))
        {
            var ld = InsulationSearch.LayerDesign(d, p, lr.TubeLayer, new SolverResult(), null, out _);
            Assert.True(ld.TabThickMm.SequenceEqual(lr.PlateThickMm), $"管保温 {InsulationSearch.Mm(lr.TubeLayer):0.0} mm：RunLayer 板厚 {string.Join("/", lr.PlateThickMm)} ≠ LayerDesign {string.Join("/", ld.TabThickMm)} —— 搜索链路没走 LayerDesign");
            Assert.True(ld.TongueThickMm.SequenceEqual(lr.TongueThickMm), $"管保温 {InsulationSearch.Mm(lr.TubeLayer):0.0} mm：RunLayer 舌片厚 {string.Join("/", lr.TongueThickMm)} ≠ LayerDesign {string.Join("/", ld.TongueThickMm)}");
        }
        Say("");
        Say("════════════════ 完整报告 ════════════════");
        foreach (var line in rep.Lines) Say(line);
        Say("");
        Say("════════════════ 判读（跑前写死） ════════════════");
        return (rep, Say);
    }

    /// <summary>链的末轮（收敛时即终点那一轮）。</summary>
    private static InsulationSearch.RoundRecord? LastRound(InsulationSearch.ChainResult c) => c.Rounds.Count > 0 ? c.Rounds[^1] : null;

    [Fact]
    public void 小规模第二版_管保温三层_圆盘舌0到12mm_粗筛()
    {
        var sw = Stopwatch.StartNew();
        var (rep, Say) = RunSearch("R48_保温搜索_小规模_第二版_2026-09-15.txt", new[] { 15, 16, 17 }, EmptyTubeSetpoint.RampTarget, "R48 保温搜索·小规模第二版（审查修改后）");
        bool valid = true;
        foreach (var lr in rep.Layers)
        {
            if (lr.Starts is null) { Say($"管保温 {InsulationSearch.Mm(lr.TubeLayer):0.0} mm：没算（{lr.SkipWhy}）"); valid = false; continue; }
            foreach (var ch in new[] { lr.Starts.Low, lr.Starts.High })
            {
                var last = LastRound(ch);
                if (last is null || !last.LineOk)
                {
                    valid = false;
                    Say($"J0 ✗ 管保温 {InsulationSearch.Mm(lr.TubeLayer):0.0} mm {ch.StartName}：末轮整线或管侧响应没解出（{last?.Why ?? "没有轮"}）⇒ 本跑作废");
                }
            }
        }
        Say($"J0 本跑有效：{(valid ? "是（每层两条链的末轮都解出）" : "否 ⇒ 本跑作废，下面的判读只作排查用")}");
        foreach (var lr in rep.Layers.Where(l => l.Starts is not null))
        {
            var st = lr.Starts!;
            string j1 = st.StartIndependent ? "与起点无关" : st.Low.Status == InsulationSearch.ChainStatus.Converged && st.High.Status == InsulationSearch.ChainStatus.Converged
                ? "结果依赖起点" : "与起点无关证不出";
            bool j2 = lr.RecipeChecks.Count > 0 && lr.RecipeChecks.All(c => c.Contains("逐位相同")) && lr.TubeBaseChecks.All(c => !c.Contains("对不上"));
            Say($"管保温 {InsulationSearch.Mm(lr.TubeLayer):0.0} mm：J1 {j1}；J2 配方自检 {lr.RecipeChecks.Count} 条、管侧复现 {lr.TubeBaseChecks.Count} 条，{(j2 ? "全部逐位相同" : "有对不上")}；可行 {(st.Feasible ? "是" : "否")}；铂重 {lr.MassG:0} g");
            // J5
            foreach (var (chain, list) in lr.Closure)
            {
                var all = list.SelectMany(t => t.Rows).ToList();
                int flagged = all.Count(r => r.Flag);
                var lastWithShift = list.Where(t => t.Rows.Any(r => r.Quantity.EndsWith("管根", StringComparison.Ordinal) && Math.Abs(r.PredShift) > 1e-9 && !double.IsNaN(r.Error))).LastOrDefault();
                string worst = "";
                if (lastWithShift.Rows is not null)
                {
                    var w = lastWithShift.Rows.Where(r => r.Quantity.EndsWith("管根", StringComparison.Ordinal) && Math.Abs(r.PredShift) > 1e-9 && !double.IsNaN(r.Error))
                                              .OrderByDescending(r => Math.Abs(r.Error)).First();
                    worst = $"；最后一次有预测修正的比较（第 {lastWithShift.Round} 轮）管根误差最大：片{w.Plate} {InsulationSearch.StateNames[w.State]} {w.Quantity} 预测修正 {w.PredShift:+0.00;-0.00} 实际 {w.ActualShift:+0.00;-0.00} 误差 {w.Error:+0.00;-0.00} K";
                }
                Say($"   J5 {chain}：闭合核对 {list.Count} 次比较、{all.Count} 行、标 ⚠ {flagged} 行{worst}"
                  + (flagged > 0 ? " ⇒ 闭合误差可能改变别的格点的排序（终点格点自己的判据值不经闭合）" : ""));
            }
            // J7
            var fr = st.StartIndependent ? st.Low.FinalRound : LastRound(st.Low);
            if (fr?.Detail is InsulationSearch.RoundDetail det && fr.LineOk)
            {
                double maxBias = Enumerable.Range(0, det.PS.GetLength(0)).SelectMany(j => Enumerable.Range(0, 2).Select(s => det.PS[j, s]))
                                           .Where(ps => ps is not null && !double.IsNaN(ps.MeshBiasK)).Select(ps => Math.Abs(ps.MeshBiasK)).DefaultIfEmpty(double.NaN).Max();
                Say($"   J7 {(st.StartIndependent ? "终点" : "起点全 0 层末轮")} |网格偏差折开尔文| 最大 {maxBias:0.00} K{(maxBias > 2 ? " ⇒ 缺口数带这一截不确定度" : "")}");
            }
        }
        // J6
        var l15 = rep.Layers.FirstOrDefault(l => l.TubeLayer == 15);
        if (l15?.Starts is not null)
            Say($"J6 与第一版对照（7.5 mm）：第二版终点 {(l15.Starts.Final?.Describe() ?? "（与起点无关证不出）")}；第一版 {SmallRunV1T15Final.Describe()} ⇒ "
              + (l15.Starts.Final is null ? "第二版没有唯一终点，不比" : l15.Starts.Final.Equals(SmallRunV1T15Final) ? "相同" : "不同 ⇒ 配方改了（网格半径、γ、量化步长），不是重跑噪声"));
        if (rep.Recommended is null)
        {
            Say("J3：三层都没有「与起点无关且每片两态可行」。⚠ 上界 12 mm 无出处、整线用导航档、终点格点判据值带网格偏差与耦合容差 0.25 K、「最近格点」带闭合误差。缺口（按归一最小裕度从大到小，最前面的缺口最小）：");
            var gaps = rep.Layers.Where(l => l.Starts is not null)
                .SelectMany(l => new[] { l.Starts!.Low, l.Starts!.High }.Where(c => c.Rounds.Count > 0 && c.Rounds[^1].LineOk)
                    .SelectMany(c => c.Rounds[^1].Choices.Where(ch => ch is not null && !ch.IsFeasible)
                        .Select(ch => (Layer: l.TubeLayer, Chain: c.StartName, Rec: c.Rounds[^1], Ch: ch))))
                .OrderByDescending(t => t.Ch.Chosen.MinNormMargin).ThenBy(t => t.Layer).ThenBy(t => t.Chain, StringComparer.Ordinal).ThenBy(t => t.Ch.Plate)
                .ToList();
            foreach (var (layer, chain, rec, ch) in gaps)
            {
                var w = ch.Chosen.Worst;
                string bias = rec.Detail is InsulationSearch.RoundDetail rd
                    ? string.Join("、", Enumerable.Range(0, 2).Select(s => rd.PS[ch.Plate, s]).Where(ps => ps is not null).Select(ps => $"{InsulationSearch.StateNames[ps.Wp.State]} {ps.MeshBiasK:+0.00;-0.00} K"))
                    : "";
                string cap = InsulationSearch.CapNote(ch.Chosen.Disc, ch.Chosen.Tab, new InsulationSearch.Options { DiscLayerMax = 24, TabLayerMax = 24 });
                Say($"   管保温 {InsulationSearch.Mm(layer):0.0} mm（{chain}末轮）片{ch.Plate}：最近格点 圆盘 {InsulationSearch.Mm(ch.Chosen.Disc):0.0}／舌 {InsulationSearch.Mm(ch.Chosen.Tab):0.0} mm{(cap.Length > 0 ? "（" + cap + "）" : "")}，"
                  + $"归一最小裕度 {ch.Chosen.MinNormMargin:+0.000;-0.000}，缺口判据 {(w is null ? ch.Chosen.Why : w.Show())}；网格偏差折 {bias}");
            }
            if (gaps.Count == 0) Say("   （没有一条链的末轮解得出，缺口列不出 —— 看上面每层的「判不了」原因）");
        }
        else
        {
            Say($"J4：建议管保温 {InsulationSearch.Mm(rep.Recommended.TubeLayer):0.0} mm（{rep.Recommended.MassG:0} g）；只在导航档工作点 + h=1.0 粗筛上成立，交付前要在 0.5 mm 上复算。");
        }
        Say($"总耗时 {sw.Elapsed.TotalMinutes:0.0} 分");
        Assert.Equal(3, rep.Layers.Count);
        Assert.True(valid, "J0：有层有链的末轮整线或管侧响应没解出 ⇒ 本跑作废");
    }
}
