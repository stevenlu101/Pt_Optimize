using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★ R48 空管到温稳态探针（2026-09-14，Opus 5 写）：**同一个设计（第二批 B2），带玻璃稳态与空管到温稳态各跑整线，逐片对照。**
///
/// 用户 2026-09-14：设备到温后、进玻璃前的空管保温「以稳态计算」—— 与带玻璃稳态并列、全部判据都要过的工况。
/// 空管 = LineCase.EmptyTube（段解与无法兰基线把产量与管内玻璃换热置 0，法兰场解与判据照常，见 LineRunner.Run）。
///
/// ══ 设计（同 R48SecondBatchTests 的 B2，逐项照抄那一档的输入，不是照抄配方）
///   W08；板厚 0.73/1.26/1.26/0.73；环倍率全 1；管保温 7.5；逐片圆盘保温 10.5/3.5/5.0/10.0；逐片舌保温 5.0/3.5/5.0/10.5；
///   舌片厚 SizeTongues 按设计电流重算（设计电流只读板件与 Base，与稳态工况无关 —— 舌片厚在造算例之前算，**两档相同是结构使然，不是证据**；
///   尺寸链与工况无关的真门是 R48EmptyTubeGateTests 的双工况逐位比较）；
///   共用片抽热各半（SplitSharedFlangeDraw = true）；0.5 mm、细区半径 MeshVerify.RequiredMeshFor；耦合容差 0.25 K、最多 4000 轮。
///
/// ══ 第一轮（2026-09-14 21:53～22:07，两份：带玻璃、空管沿用生产设定）—— 跑前预测与跑后对账，原样留档
///   【物理把关人（推理）】空管时管子 hP 约 16 → 6.6 W/(m·K)，接头 K/W 约升到 3.8（约 1.6 倍），电流约低 10 %、法兰焦耳热约低 20 %。
///   【Opus 5 跑前自审（推理）】hP：hg·π·D = 65 × π × 0.050 = 10.2 ⇒ 16 → 6.6 相符；K/W ∝ 1/√(kAβ) ⇒ √(16/6.6) ≈ 1.56 倍。
///     电流方向可能相反：玻璃从 1150 °C 进，第 2、3 段玻璃比管热、在给管子热 ⇒ 空管第 2、3 段电流预计升几 %。
///   【跑后对账（出处 deliverable\R48_空管稳态_差值_2026-09-14.txt）】
///     · hP 17.41/16.94/16.75 → 7.20/6.73/6.54，三段都差 10.21 ⇒ 16 → 6.6 对。接头 K/W 2.31～2.51 → 4.12～4.67（1.65～1.89 倍）⇒ 方向对、实测略大。
///     · 电流 HC1 −0.01 %、HC2 +9.88 %、HC3 +14.45 %；法兰焦耳热 入口 −0.14 %、共用 +12.12 %／+34.18 %、出口 +41.78 % ⇒「电流约低 10 %」方向错。
///   【审查后改口（2026-09-14 Opus 5，R48 审查意见五条要紧的）】第一轮的交接结论要降级，理由：
///     1. 空管控温点**沿用了生产设定 1150/1080/1050**，而用户 09-08 定过「升温目标全线 1150 °C」—— 「到温」按那个读就是全线 1150。两种读法都要跑，待用户定。
///     2. 「热偶读数口径」的共用片基准用了两侧设定的较大值，不是用户 09-14 定的**对数平均**；那几个超温数是手算的、不是仪器打的。
///        本轮起逐片打印「最热铂 − 热偶读数基准」，基准调 ThermocoupleBasis（生产公开函数）。
///     3. 模型**不含**空管才有的管腔内轴向辐射与管口辐射散热 ⇒ 空管结论一律是「不含这两项的模型结果」；本轮加管腔辐射 kA 的两档敏感度。
///     4. 「升温链两工况逐位相同，已实证」不成立：证据（舌片厚）是空转的；「升温期法兰−管峰值」在第一轮差值文件里就 32.943 → 35.066 K。
///     5. 端部额外保温的形状原随工况变（B2 本身不装端部额外保温，主探针不受影响；附带的端部保温探针受影响，本轮重跑）。
///
/// ══ 第二轮（审查修改轮，同一段代码并行跑五份）—— 跑前预测（2026-09-14 Opus 5，推理，未实测；跑完照数对账，写在交接里）
///   A. 带玻璃：与第一轮带玻璃逐片相同（本轮改动对带玻璃逐位不变，快门的指纹守着）；共用片「最热铂 − 对数平均基准」HC1|HC2 ≈ +30.9、HC2|HC3 ≈ +20.8 K
///      （由第一轮舌区峰 1145.61／1085.70 与基准 1114.71／1064.94 推出）。
///   B. 空管·沿用生产设定：与第一轮空管逐片相同；「最热铂 − 基准」HC1|HC2 ≈ +78.4、HC2|HC3 ≈ +139.0、出口 ≈ +135.2 K。
///   C. 空管·全线升温目标 1150：三段同一根管、同保温、同设定 ⇒ 下游两段电流升到约与 HC1 相同（约 1070 A），梯度推热没了；
///      各片「最热铂 − 1150」主要剩法兰自热高出管根的那一截（第一轮空管里共用片与出口片约高出管根 +39～+51 K，入口片 +14 K）⇒
///      预计共用片与出口片约 +40～+60 K、入口片约 0～+15 K。结论形态：仍超 5 K，但比沿用生产设定小一半以上。
///   D/E. 空管·沿用生产设定 + 管腔辐射 kA 0.023／0.056 W·m/K（DesignInputs.TubeCavityRadKAWmPerK 注释里的两档估计 —— 2026-09-14 旧钩子时期的预测；
///        该属性 R48 G3 起改名 DesignInputs.TubeCavityRadKA1150WmPerK、改成空管才有且按 T³ 换算，2026-09-15 Opus 5 注）：
///      轴向总 kA 从 0.0106 升到约 0.034／0.066（3.2／6.3 倍）⇒ 接头 K/W ∝ 1/√kA 降到第一轮空管的约 0.56／0.40 倍（4.1 → 约 2.3／1.6 K/W）；
///      下游管根被抬高的量（第一轮增量温降约 −88 K）按同一比例缩到约 −50／−35 K；「最热铂 − 基准」的 HC2|HC3 由 +139 降到约 +100～+120 K
///      （基准 1064.94 与 HC3 设定梯度那部分不随辐射变）。结论形态：幅度明显变，方向不变。
///
/// ══ 第二轮跑后对账（2026-09-15 凌晨跑，签 2026-09-14 Opus 5；出处 deliverable\R48_空管稳态_*_2026-09-15_*.txt，数只抄「最热铂−基准」一栏，K）
///   A 带玻璃（…_带玻璃_2026-09-15_000802）：入口 +3.31　HC1|HC2 +30.90　HC2|HC3 +20.75　出口 −2.61 ⇒ 预测对（与第一轮逐片相同）。
///     ⚠ 按对数平均基准，**带玻璃时两片共用片也超 5 K**（第一轮交接按较大值说「几乎过」是错的口径）。
///   B 空管·生产设定（…_空管_生产设定_2026-09-15_000811）：−0.12　+78.41　+138.97　+135.15 ⇒ 预测对。
///   C 空管·全线 1150（…_空管_全线升温目标_2026-09-15_000815）：−1.62　+80.91　+157.97　+161.06 ⇒ **预测错，方向反了**（预测 +40～+60、比 B 小一半以上，实际比 B 还大）。
///     错在哪：我把 B 里下游管根被抬高归给「设定梯度推热」，可增量温降是对**无法兰基线**量的，梯度早就在基线里扣掉了 ——
///     抬高管根的是法兰自热经管孔倒灌（净流入 B −42.9 W、C −49.7 W，接头约 4 K/W）。全线 1150 时下游段电流升（HC2 1023.7 → 1067.8 A、
///     HC3 1003.4 → 1066.1 A），法兰焦耳热跟着升（HC2|HC3 648 → 752 W），管根抬得更高（HC3 B 端高出设定 +88.5 → +104.1 K）。
///     ⇒ 审查第 1 条担心的「换成全线 1150 后结论形态可能不同」在本模型里不成立：下游片按热偶读数口径超温，两种控温点读法都成立，全线 1150 更重。
///   D/E 管腔辐射敏感度：0.5 mm 两档在无法兰基线里跑了 34 分钟一行进度都没有（那时基线不报进度），停掉，改在**开箱网格同网格对照**
///     （…_空管_生产设定_开箱网格_腔辐射kA0.000／0.023／0.056_2026-09-15_0050xx）：
///       kA 0　　：入口 +4.35　HC1|HC2 +85.08　HC2|HC3 +147.12　出口 +143.20　共用接头 4.12～4.14 K/W　HC3 A 端增量温降 −90.96 K　基线约 26 轮
///       kA 0.023：入口 +11.04　+69.77　+106.59　+101.09　1.81～1.86 K/W　−45.98 K　基线约 257 轮
///       kA 0.056：入口 +17.03　+63.21　+87.03　+82.44　0.98～1.10 K/W　−25.76 K　基线约 367 轮
///     对账：K/W 预测降到 0.56／0.40 倍，实测 0.44～0.45／0.24～0.27 倍（共用与出口接头，降得更多）；管根抬高预测 −50／−35，实测 −46／−26；HC2|HC3 超温降 40／60 K（同网格）。
///     **没预测到的**：入口片随辐射变热（+4 → +11／+17 K，跨过 5 K）—— 轴向导热变强后入口段 A 端管根没那么冷了（1137 → 1144／1150 °C）。
///     结论形态：下游三片仍远超 5 K（方向不变），幅度对管腔辐射很敏感（HC2|HC3 +147 → +87 K）；入口片的过与不过取决于辐射模型。
///     副产：kA 越大无法兰基线外层收敛越慢（26 → 257 → 367 轮，基线外层没有加速），0.5 mm 上每轮还要解四片场，估计数小时，没有跑。
///     开箱网格与 0.5 mm 的差（kA 0：HC2|HC3 +147.12 vs +138.97）约 8 K，同网格三档的差值有意义，绝对数不与 0.5 mm 混用。
///
/// ══ 输出（审查第 10 条：文件名带运行时刻，已存在就拒绝写，今天归档的证据文件不会被重跑覆盖）
///   deliverable\R48_空管稳态_{工况}_{yyyy-MM-dd_HHmmss}.txt／.json；「两工况差值_读结果」读各工况最新一份 JSON，
///   只在两份是**同一份编译产物**（核心与测试程序集的 MVID 都相同）且时刻相差不超过 24 h 时才比，否则断言失败、不写差值。
///   五个整线 Fact 各约 13～20 分钟，可分进程并行；差值 Fact 只读文件。
/// </summary>
[Trait("速度", "慢")]
public class R48EmptyTubeStateTests
{
    private readonly ITestOutputHelper _out;
    public R48EmptyTubeStateTests(ITestOutputHelper o) { _out = o; }

    // ── 管腔辐射敏感度两档（出处：DesignInputs.TubeCavityRadKA1150WmPerK 注释里的估算：ℓ = 40 mm 串联表面热阻 0.023；与 ℓ 自洽 0.056）
    // ⚠ R48 G3（2026-09-15，Opus 5）：钩子改成了模型输入 DesignInputs.TubeCavityRadKA1150WmPerK（空管才有、按段控温点温度 T³ 换算、
    //   接头导度那一份按邻段端温取；生产默认低档 0.023）。本探针各 Fact 照旧**显式**给系数（带玻璃／生产设定／全线升温目标三档给 0 = 不计管腔辐射，
    //   即 2026-09-14 那一版的模型）。⇒ 给 0 时**管腔辐射这一项**与 2026-09-15 凌晨同口径；给 0.023／0.056 时这一项也不同口径
    //   （控温点 1080／1050 两段系数 ×0.860／×0.804，接头导度按邻段端温取），新口径的两档见 R48EmptyTubeContinuationTests（全线 1150、0.5 mm）。
    // ⚠ R48 G3 审查后改口（2026-09-15，Opus 5）：此前这里写「给 0 的各档复现 2026-09-15 凌晨的数」—— **不对**。那批数出自合并前的 r48_C 树
    //   （合并 A 压接整面接触、D 逐片保温之前），合并后整线法兰场已变（升温期法兰−管峰值、抽热、电流都不同，
    //   见 deliverable\R48_G3_C路快门_合并后改前_2026-09-15.txt 与 r48_C 快门输出的逐项对照）⇒ 系数给 0 重跑，**整线数也不复现、不能与凌晨那批混比**。
    private const double RadKALow = SegmentSolver.CavityRadKA1150Low, RadKAHigh = SegmentSolver.CavityRadKA1150High;

    private const string TagGlass = "带玻璃", TagEmptyProd = "空管_生产设定", TagEmptyRamp = "空管_全线升温目标";
    private static string TagRad(double ka) => $"空管_生产设定_腔辐射kA{ka:0.000}";
    private static string TagRadCoarse(double ka) => $"空管_生产设定_开箱网格_腔辐射kA{ka:0.000}";

    [Fact] public void 带玻璃稳态_B2() => Run(TagGlass, emptyTube: false);
    [Fact] public void 空管到温稳态_生产设定_B2() => Run(TagEmptyProd, emptyTube: true, EmptyTubeSetpoint.AsGiven);
    [Fact] public void 空管到温稳态_全线升温目标_B2() => Run(TagEmptyRamp, emptyTube: true, EmptyTubeSetpoint.RampTarget);
    // ⚠ 下面两档 0.5 mm 管腔辐射**没有跑完**（2026-09-15 00:08 起跑，34 分钟仍在无法兰基线里，被停）。开箱网格上同一工况基线要 257／367 轮，
    //   0.5 mm 每轮多四片细网格场解 ⇒ 估计数小时。要细网格复核再跑；幅度已由下面开箱网格三档同网格对照给出。
    [Fact] public void 空管到温稳态_生产设定_腔辐射低档_B2() => Run(TagRad(RadKALow), emptyTube: true, EmptyTubeSetpoint.AsGiven, RadKALow);
    [Fact] public void 空管到温稳态_生产设定_腔辐射高档_B2() => Run(TagRad(RadKAHigh), emptyTube: true, EmptyTubeSetpoint.AsGiven, RadKAHigh);

    // ── 管腔辐射敏感度·开箱网格（2026-09-14 Opus 5，审查修改轮）：上面两档 0.5 mm 的在无法兰基线里跑了 34 分钟一行进度都没有（那时基线不报进度），
    //   看不出是慢还是不收敛，按「方向不明立刻停」停掉。先在开箱网格上同网格对照（无／低／高三档只差钩子）看基线要几轮、效应多大，
    //   再决定 0.5 mm 值不值得跑。同网格相对比较：网格误差对三档同向，差值有意义；绝对数不与 0.5 mm 的混用。
    [Fact] public void 腔辐射敏感度_开箱网格_无() => Run(TagRadCoarse(0), emptyTube: true, EmptyTubeSetpoint.AsGiven, 0, fineMesh: false);
    [Fact] public void 腔辐射敏感度_开箱网格_低档() => Run(TagRadCoarse(RadKALow), emptyTube: true, EmptyTubeSetpoint.AsGiven, RadKALow, fineMesh: false);
    [Fact] public void 腔辐射敏感度_开箱网格_高档() => Run(TagRadCoarse(RadKAHigh), emptyTube: true, EmptyTubeSetpoint.AsGiven, RadKAHigh, fineMesh: false);

    // ── 结果摘要（每工况存一份 JSON，差值从这里读 —— 只比同一份编译产物跑出来的两份）
    public sealed class SegRow
    {
        public string Name { get; set; } = "";
        public double Set { get; set; }
        public double I { get; set; }
        public double P { get; set; }
        public double TA { get; set; }
        public double TB { get; set; }
        public double BaseA { get; set; }
        public double BaseB { get; set; }
        public double DipA { get; set; }
        public double DipB { get; set; }
        public double Beta { get; set; }
        public double Lt { get; set; }
    }
    public sealed class PlateRow
    {
        public string Name { get; set; } = "";
        public double I { get; set; }
        public double Q { get; set; }
        public double QGen { get; set; }
        public double QLoss { get; set; }
        public double TRoot { get; set; }
        public double DiscMax { get; set; }
        public double DiscMaxR { get; set; }
        public double TabMax { get; set; }
        public double TabMaxR { get; set; }
        public double QClamp { get; set; }
        /// <summary>热偶读数基准 °C（ThermocoupleBasis.ReferenceC）</summary>
        public double Ref { get; set; }
        /// <summary>最热铂 − 热偶读数基准 K（ThermocoupleBasis.HottestMinusReferenceK）</summary>
        public double HotMinusRef { get; set; }
    }
    public sealed class CheckRow
    {
        public string Name { get; set; } = "";
        public double Actual { get; set; }
        public double Limit { get; set; }
        public bool Ok { get; set; }
        public bool Undetermined { get; set; }
        public string Where { get; set; } = "";
    }
    public sealed class Summary
    {
        public string Tag { get; set; } = "";
        public bool EmptyTube { get; set; }
        public string SetpointFrom { get; set; } = "";
        public double RadKA { get; set; }
        public string Stamp { get; set; } = "";
        public string When { get; set; } = "";
        public string CoreMvid { get; set; } = "";
        public string TestMvid { get; set; } = "";
        public bool Converged { get; set; }
        public double RemainK { get; set; }
        public bool AllOk { get; set; }
        public double MassG { get; set; }
        public double[] TongueThickMm { get; set; } = Array.Empty<double>();
        public List<SegRow> Segs { get; set; } = new();
        public List<PlateRow> Plates { get; set; } = new();
        public List<CheckRow> Checks { get; set; } = new();
        public List<string> Notes { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string CoreMvid => typeof(LineRunner).Assembly.ManifestModule.ModuleVersionId.ToString();
    private static string TestMvid => typeof(R48EmptyTubeStateTests).Assembly.ManifestModule.ModuleVersionId.ToString();

    /// <summary>新文件路径：带运行时刻；已存在就拒绝（审查第 10 条：不许覆盖 deliverable 下已有文件）。</summary>
    private static string NewFile(string tag, string stamp, string ext)
    {
        string f = Path.Combine(Root(), "deliverable", $"R48_空管稳态_{tag}_{stamp}.{ext}");
        Assert.False(File.Exists(f), $"{f} 已存在 —— 拒绝覆盖（换个时刻重跑）");
        return f;
    }

    private void Run(string tag, bool emptyTube, EmptyTubeSetpoint sp = EmptyTubeSetpoint.AsGiven, double radKA = 0, bool fineMesh = true)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string file = NewFile(tag, stamp, "txt"), jsonFile = NewFile(tag, stamp, "json");
        var sb = new StringBuilder();
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        var p = new DesignInputs { SplitSharedFlangeDraw = true, TubeCavityRadKA1150WmPerK = radKA };   // R48 G3：显式给（生产默认已是 0.023，给 0 = 不计）
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.TubeInsulMm = 7.5;
        d.DiscInsulMm = new[] { 10.5, 3.5, 5.0, 10.0 };
        d.TabInsulMm = new[] { 5.0, 3.5, 5.0, 10.5 };
        d = d.Fit();
        d.SizeTongues(p);
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        var lc = d.BuildCase(p, checkRamp: false, emptyTube: emptyTube, emptyTubeSetpoint: sp);
        if (fineMesh) MeshAdapt.RefineWholeMesh(lc, 0.5, radius);
        lc.CoupleTolK = 0.25;
        lc.CoupleMaxRounds = Math.Max(lc.CoupleMaxRounds, 4000);
        string meshNote = fineMesh ? $"0.5 mm 细区半径 {radius:0}" : $"开箱网格（细 {lc.MeshFineMm:0.###} mm，只作同网格相对比较）";
        Say($"R48 空管稳态探针 【{tag}】（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　设计同第二批 B2　{meshNote}　耦合容差 {lc.CoupleTolK} K　共用片抽热{(lc.Base.SplitSharedFlangeDraw ? "各半" : "双扣")}");
        Say($"编译产物 核心 MVID {CoreMvid}　测试 MVID {TestMvid}");
        Say($"算例工况位 EmptyTube = {lc.EmptyTube}（设定 {emptyTube}）{(lc.EmptyTube != emptyTube ? "　⚠⚠ **与设定不符 —— 接线没接上，本档作废**" : "")}"
          + $"　控温点 {string.Join("/", lc.SetpointC.Select(v => v.ToString("0.#")))} °C（来源 {lc.EmptyTubeSetpointFrom}）"
          + $"　Base 产量 {lc.Base.ThroughputTPerDay} t/d、hg {lc.Base.HGlass}（段解里空管才置 0，Base 本身不改）　玻璃进口 {lc.GlassInC} °C");
        Say($"管腔轴向辐射系数（1150 °C 时 kA）{lc.Base.TubeCavityRadKA1150WmPerK:0.000} W·m/K（0 = 不计，只有管壁导热；2026-09-15 起生产默认 {SegmentSolver.CavityRadKA1150Low:0.000}）　端部额外保温 {lc.Base.EndInsulExtraMm:0.0} mm");
        Say($"管保温层 {lc.Base.Layer1.ThicknessMm:0.0}　板件圆盘保温 {string.Join("/", lc.FlangePlates.Select(g => g.DiscInsulThickMm.ToString("0.0")))}　"
          + $"板件舌保温 {string.Join("/", lc.FlangePlates.Select(g => g.TabInsulThickMm.ToString("0.0")))}　舌片厚 {string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.00")))} mm");

        var sw = System.Diagnostics.Stopwatch.StartNew(); double last = 0;
        double every = fineMesh ? 180 : 60;
        var tick = new Progress<string>(m =>
        {
            if (sw.Elapsed.TotalSeconds - last < every) return;
            last = sw.Elapsed.TotalSeconds;
            Say($"     · [已跑 {sw.Elapsed.TotalSeconds:0} s] {m}");
        });
        var r = LineRunner.Run(lc, tick, default);
        Assert.True(r.Ok, "整线解不出来：" + r.Message);
        Say($"用时 {sw.Elapsed.TotalMinutes:0.0} 分　耦合收敛 {r.Converged}　剩余估计 {r.CoupleRemainK:0.000} K　合计铂重 {r.TotalMassG:0} g　全过 {r.AllOk}");

        var sum = new Summary
        {
            Tag = tag, EmptyTube = lc.EmptyTube, SetpointFrom = lc.EmptyTubeSetpointFrom.ToString(), RadKA = radKA,
            Stamp = stamp, When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), CoreMvid = CoreMvid, TestMvid = TestMvid,
            Converged = r.Converged, RemainK = r.CoupleRemainK, AllOk = r.AllOk, MassG = r.TotalMassG,
            TongueThickMm = (double[])d.TongueThickMm.Clone()
        };
        Say("   段（设定／电流／功率／管根两端与其减设定／无法兰基线两端／增量温降两端／管 hP／热衰减长度）：");
        foreach (var s in r.Segments)
        {
            sum.Segs.Add(new SegRow
            {
                Name = s.Name, Set = s.SetpointC, I = s.CurrentA, P = s.PowerW, TA = s.TRootAC, TB = s.TRootBC,
                BaseA = s.BaseTRootAC, BaseB = s.BaseTRootBC, DipA = s.FlangeDipAK, DipB = s.FlangeDipBK,
                Beta = s.BetaWPerMK, Lt = s.DecayLengthMm
            });
            Say($"     {s.Name}　设定 {s.SetpointC:0.0}　电流 {s.CurrentA:0.0} A　功率 {s.PowerW:0.0} W　管根 A/B {s.TRootAC:0.00}/{s.TRootBC:0.00}（减设定 {s.TRootAC - s.SetpointC:+0.00;-0.00}/{s.TRootBC - s.SetpointC:+0.00;-0.00}）　"
              + $"基线 A/B {s.BaseTRootAC:0.00}/{s.BaseTRootBC:0.00}　增量温降 A/B {s.FlangeDipAK:+0.000;-0.000}/{s.FlangeDipBK:+0.000;-0.000} K　"
              + $"hP {s.BetaWPerMK:0.00} W/(m·K)　热衰减长度 {s.DecayLengthMm:0.0} mm　玻璃进/出 {Fmt(s.GlassInC)}/{Fmt(s.GlassOutC)}");
        }
        Say("   片（电流／抽热／自身焦耳热／自身散热／贴的管根／盘峰／舌区峰／进铜排夹的热／热偶读数基准与最热铂减基准）：");
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            var f = r.Flanges[j];
            double refC = ThermocoupleBasis.ReferenceC(r.Segments.Select(s => s.SetpointC).ToArray(), j);
            double hot = ThermocoupleBasis.HottestMinusReferenceK(r, j);
            sum.Plates.Add(new PlateRow
            {
                Name = f.Name, I = f.CurrentA, Q = f.QFromTubeW, QGen = f.QGenW, QLoss = f.QLossW, TRoot = f.TRootC,
                DiscMax = f.TDiscMaxC, DiscMaxR = f.DiscMaxRMm, TabMax = f.TTabMaxC, TabMaxR = f.TabMaxRMm, QClamp = f.QClampW,
                Ref = refC, HotMinusRef = hot
            });
            Say($"     {f.Name}　电流 {f.CurrentA:0.0} A　抽热 {f.QFromTubeW:+0.000;-0.000} W　焦耳热 {f.QGenW:0.00} W　散热 {f.QLossW:0.00} W　管根 {f.TRootC:0.00}　"
              + $"盘峰 {f.TDiscMaxC:0.00}（r={f.DiscMaxRMm:0.0}）　舌区峰 {f.TTabMaxC:0.00}（r={f.TabMaxRMm:0.0}）　进夹 {f.QClampW:0.00} W　"
              + $"热偶读数基准 {refC:0.00}（{(j == 0 || j == r.Segments.Length ? "端片＝本段设定" : "共用片＝两侧设定的对数平均，开尔文里取")}）　最热铂−基准 {hot:+0.00;-0.00} K");
        }
        Say("   ⚠「最热铂 − 基准 ≤ 5 K」是物理把关人第八轮提的口径，**还没写进判据代码**、也还要请用户确认；这里只报数。");
        Say("   接头的「增量温降 ÷ 该侧抽热」K/W（共用片抽热各半 ⇒ 每侧按一半算；抽热近 0 时比值无意义，照印）：");
        foreach (var line in JointRatios(sum)) Say("     " + line);
        Say("   判据全表：");
        foreach (var c in r.Checks)
        {
            sum.Checks.Add(new CheckRow { Name = c.Name, Actual = c.Actual, Limit = c.Limit, Ok = c.Ok, Undetermined = c.Undetermined, Where = c.Where });
            Say($"     {(c.Undetermined ? "判不了" : c.Ok ? "过　" : "不过")}　{Criteria.Plain(c.Name)}　{Fmt(c.Actual)} {c.Unit}（限 {(c.LessIsBetter ? "≤" : "≥")} {c.Limit:0.###}，{c.Where}）"
              + (c.Undetermined ? $"　{c.Note}" : ""));
        }
        Say("   Notes：");
        foreach (var n in r.Notes) { sum.Notes.Add(n); Say("     " + n); }
        File.WriteAllText(jsonFile, JsonSerializer.Serialize(sum, JsonOpts), new UTF8Encoding(false));
    }

    /// <summary>
    /// 读各工况最新一份 JSON 写差值。成对比较只在两份出自**同一份编译产物**（核心、测试 MVID 都相同）且时刻相差 ≤ 24 h 时进行；
    /// 任一对不可比 ⇒ 断言失败（审查第 10 条）。缺的工况跳过并印出来。
    /// </summary>
    [Fact]
    public void 两工况差值_读结果()
    {
        var pairs = new (string A, string B, string Why)[]
        {
            (TagGlass, TagEmptyProd, "只差工况位（同一控温点）"),
            (TagGlass, TagEmptyRamp, "工况位与控温点都不同（带玻璃是生产设定，空管是全线升温目标）"),
            (TagEmptyProd, TagEmptyRamp, "同是空管，只差控温点读法"),
            (TagEmptyProd, TagRad(RadKALow), "同是空管·生产设定，只差管腔辐射 kA 钩子（低档）"),
            (TagEmptyProd, TagRad(RadKAHigh), "同是空管·生产设定，只差管腔辐射 kA 钩子（高档）"),
            (TagRadCoarse(0), TagRadCoarse(RadKALow), "开箱网格·空管·生产设定，只差管腔辐射 kA 钩子（低档）"),
            (TagRadCoarse(0), TagRadCoarse(RadKAHigh), "开箱网格·空管·生产设定，只差管腔辐射 kA 钩子（高档）"),
        };
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string file = NewFile("差值", stamp, "txt");
        var sb = new StringBuilder();
        void Say(string s) { _out.WriteLine(s); sb.AppendLine(s); }
        Say($"R48 空管稳态探针 差值（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　差 = 后者 − 前者；% = 差 ÷ 前者");
        var bad = new List<string>();
        int done = 0;
        foreach (var (ta, tb, why) in pairs)
        {
            var (fa, a) = Latest(ta); var (fb, b) = Latest(tb);
            if (a is null || b is null) { Say($"── {ta} → {tb}：{(a is null ? ta : tb)} 还没有结果，跳过"); continue; }
            bool sameBuild = a.CoreMvid == b.CoreMvid && a.TestMvid == b.TestMvid && a.CoreMvid.Length > 0;
            double hours = Math.Abs((DateTime.Parse(a.When) - DateTime.Parse(b.When)).TotalHours);
            if (!sameBuild || hours > 24)
            {
                bad.Add($"{ta} → {tb}：{(sameBuild ? "" : "编译产物不同（MVID 不一致）")}{(hours > 24 ? $" 时刻相差 {hours:0.0} h" : "")}");
                Say($"── {ta} → {tb}：⚠⚠ **不可比**（{bad[^1]}），不写差值");
                continue;
            }
            WriteDiff(Say, a, b, fa!, fb!, why);
            done++;
        }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        Assert.True(bad.Count == 0, "有不可比的两份：" + string.Join("；", bad));
        Assert.True(done > 0, "一对可比的结果都没有 —— 先跑整线 Fact");
    }

    private static (string? File, Summary? S) Latest(string tag)
    {
        var dir = Path.Combine(Root(), "deliverable");   // 只读：列目录找最新一跑（2026-09-15 Opus 5 I 路注，门 R48DeliverableWriteGuardTests）
        var pat = new Regex("^R48_空管稳态_" + Regex.Escape(tag) + @"_(\d{4}-\d{2}-\d{2}_\d{6})\.json$");
        var hit = Directory.GetFiles(dir, "R48_空管稳态_*.json")
                           .Select(f => (f, m: pat.Match(Path.GetFileName(f))))
                           .Where(x => x.m.Success)
                           .OrderByDescending(x => x.m.Groups[1].Value, StringComparer.Ordinal)
                           .FirstOrDefault();
        if (hit.f is null) return (null, null);
        return (hit.f, JsonSerializer.Deserialize<Summary>(File.ReadAllText(hit.f), JsonOpts));
    }

    private static void WriteDiff(Action<string> Say, Summary g, Summary e, string fg, string fe, string why)
    {
        Say("");
        Say($"══ {g.Tag} → {e.Tag}（{why}）");
        Say($"出处：{Path.GetFileName(fg)}（{g.When}）与 {Path.GetFileName(fe)}（{e.When}）；同一份编译产物（核心 MVID {g.CoreMvid}）。");
        bool sameGeom = g.TongueThickMm.Length == e.TongueThickMm.Length && g.TongueThickMm.Zip(e.TongueThickMm).All(t => t.First == t.Second);
        Say($"两档舌片厚逐位相同：{sameGeom}（舌片厚在造算例之前按设计电流算，结构上就该相同 —— 这不是「尺寸链与工况无关」的证据，那条的门在快门里）"
          + $"　工况位 {g.EmptyTube}/{e.EmptyTube}　控温点来源 {g.SetpointFrom}/{e.SetpointFrom}　管腔辐射 kA {g.RadKA:0.000}/{e.RadKA:0.000}"
          + (sameGeom ? "" : "　⚠⚠ **几何不同：两份不可比**"));
        Say($"耦合收敛 {g.Converged}（剩余 {g.RemainK:0.000} K）／{e.Converged}（剩余 {e.RemainK:0.000} K）　全过 {g.AllOk}／{e.AllOk}　铂重 {g.MassG:0}／{e.MassG:0} g");
        Say("   段：");
        for (int i = 0; i < Math.Min(g.Segs.Count, e.Segs.Count); i++)
        {
            var a = g.Segs[i]; var b = e.Segs[i];
            Say($"     {a.Name}　设定 {a.Set:0} → {b.Set:0}　电流 {a.I:0.0} → {b.I:0.0} A（{Pct(b.I, a.I)}）　功率 {a.P:0.0} → {b.P:0.0} W（{Pct(b.P, a.P)}）　"
              + $"hP {a.Beta:0.00} → {b.Beta:0.00} W/(m·K)（{Pct(b.Beta, a.Beta)}）　热衰减长度 {a.Lt:0.0} → {b.Lt:0.0} mm");
            Say($"           管根 A {a.TA:0.00} → {b.TA:0.00}（{b.TA - a.TA:+0.00;-0.00}）　B {a.TB:0.00} → {b.TB:0.00}（{b.TB - a.TB:+0.00;-0.00}）　"
              + $"管根减设定 A {a.TA - a.Set:+0.00;-0.00} → {b.TA - b.Set:+0.00;-0.00}　B {a.TB - a.Set:+0.00;-0.00} → {b.TB - b.Set:+0.00;-0.00}　"
              + $"基线 A {a.BaseA:0.00} → {b.BaseA:0.00}　B {a.BaseB:0.00} → {b.BaseB:0.00}　"
              + $"增量温降 A {a.DipA:+0.000;-0.000} → {b.DipA:+0.000;-0.000}　B {a.DipB:+0.000;-0.000} → {b.DipB:+0.000;-0.000} K");
        }
        Say("   片：");
        for (int j = 0; j < Math.Min(g.Plates.Count, e.Plates.Count); j++)
        {
            var a = g.Plates[j]; var b = e.Plates[j];
            Say($"     {a.Name}　电流 {a.I:0.0} → {b.I:0.0} A（{Pct(b.I, a.I)}）　焦耳热 {a.QGen:0.00} → {b.QGen:0.00} W（{Pct(b.QGen, a.QGen)}）　"
              + $"抽热 {a.Q:+0.000;-0.000} → {b.Q:+0.000;-0.000} W　盘峰 {a.DiscMax:0.00} → {b.DiscMax:0.00}（{b.DiscMax - a.DiscMax:+0.00;-0.00}）　"
              + $"舌区峰 {a.TabMax:0.00} → {b.TabMax:0.00}（{b.TabMax - a.TabMax:+0.00;-0.00}）　管根 {a.TRoot:0.00} → {b.TRoot:0.00}　"
              + $"热偶基准 {a.Ref:0.00} → {b.Ref:0.00}　最热铂−基准 {a.HotMinusRef:+0.00;-0.00} → {b.HotMinusRef:+0.00;-0.00} K");
        }
        Say("   接头 K/W（增量温降 ÷ 该侧抽热）：");
        var ga = JointRatios(g); var ea = JointRatios(e);
        for (int k = 0; k < Math.Min(ga.Count, ea.Count); k++) Say($"     {g.Tag} {ga[k]}\n     {e.Tag} {ea[k]}");
        Say("   判据：");
        foreach (var a in g.Checks)
        {
            var b = e.Checks.FirstOrDefault(x => x.Name == a.Name);
            if (b is null) { Say($"     {Criteria.Plain(a.Name)}　后者里没有这条"); continue; }
            string St(CheckRow c) => c.Undetermined ? "判不了" : c.Ok ? "过" : "不过";
            Say($"     {Criteria.Plain(a.Name)}　{Fmt(a.Actual)} {St(a)} → {Fmt(b.Actual)} {St(b)}（限 {a.Limit:0.###}，{a.Where} → {b.Where}）");
        }
    }

    /// <summary>接头「增量温降 ÷ 该侧抽热」。端片整份抽热；内部片两侧各半（与本探针 SplitSharedFlangeDraw = true 同口径）。</summary>
    private static List<string> JointRatios(Summary s)
    {
        var rows = new List<string>();
        int n = s.Segs.Count;
        for (int j = 0; j < s.Plates.Count; j++)
        {
            double q = s.Plates[j].Q;
            if (j == 0) rows.Add($"{s.Plates[j].Name}：{s.Segs[0].Name} A 端 {s.Segs[0].DipA:+0.000;-0.000} K ÷ {q:+0.000;-0.000} W = {Ratio(s.Segs[0].DipA, q)}");
            else if (j >= n) rows.Add($"{s.Plates[j].Name}：{s.Segs[n - 1].Name} B 端 {s.Segs[n - 1].DipB:+0.000;-0.000} K ÷ {q:+0.000;-0.000} W = {Ratio(s.Segs[n - 1].DipB, q)}");
            else rows.Add($"{s.Plates[j].Name}：{s.Segs[j - 1].Name} B 端 {s.Segs[j - 1].DipB:+0.000;-0.000} K ÷ {0.5 * q:+0.000;-0.000} W = {Ratio(s.Segs[j - 1].DipB, 0.5 * q)}"
                        + $"　{s.Segs[j].Name} A 端 {s.Segs[j].DipA:+0.000;-0.000} K ÷ {0.5 * q:+0.000;-0.000} W = {Ratio(s.Segs[j].DipA, 0.5 * q)}");
        }
        return rows;
    }

    private static string Ratio(double dip, double q) => Math.Abs(q) < 0.5 ? $"（抽热 |{q:0.00}| W < 0.5，不除）" : $"{dip / q:0.00} K/W";
    private static string Pct(double now, double was) => Math.Abs(was) < 1e-9 ? "—" : $"{(now - was) / Math.Abs(was) * 100:+0.00;-0.00} %";
    private static string Fmt(double v) => double.IsNaN(v) ? "不适用/NaN" : v.ToString("0.###");

    /// <summary>
    /// ★ 附带探针（2026-09-14，Opus 5；做快门时撞到的）：**空管 + 端部额外保温**的单段行为。
    /// 快门里「空管 + 两端抽热 + 邻段 + 端部额外保温 10 mm × 30 mm」反算电流时 FindCurrent 抛「在热稳定极限内无法达到设定温度」。
    /// 审查第 5 条（2026-09-14 Opus 5）：第一轮的对照里空管的保温**形状**也跟着 hg = 0 变了（不可比）。本轮三栏：
    ///   带玻璃／空管·形状按带玻璃 hg（= 整线 LineRunner 现在的写法，硬件相同）／空管·形状按自身 hg = 0（第一轮的写法，只作对照）。
    /// 输出 deliverable\R48_空管稳态_端部额外保温探针_{时刻}.txt。只记录，不断言物理结论。
    /// </summary>
    [Fact]
    public void 端部额外保温_空管单段探针()
    {
        var sb = new StringBuilder();
        void Say(string s) { _out.WriteLine(s); sb.AppendLine(s); }
        DesignInputs Mk(int mode, double extraMm)   // 0 带玻璃；1 空管·形状按带玻璃 hg；2 空管·形状按自身 hg
        {
            var p = new DesignInputs { TSetC = 1150, TGlassInC = 1150, EndInsulExtraMm = extraMm, EndInsulLengthMm = 30 };
            if (mode == 1) p.EndInsulShapeHGlass = p.HGlass;
            if (mode >= 1) { p.ThroughputTPerDay = 0; p.HGlass = 0; }
            return p;
        }
        string[] label = { "带玻璃　　　　　　", "空管·形状按带玻璃hg", "空管·形状按自身hg=0" };
        Say($"R48 空管 + 端部额外保温 单段探针（Opus 5，审查修改轮）　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　开箱 DesignInputs，设定 1150、玻璃进口 1150、端部额外保温长 30 mm，两端绝热、无邻段");
        Say($"编译产物 核心 MVID {CoreMvid}");
        foreach (double extra in new[] { 0.0, 10.0 })
        {
            Say($"── 端部额外保温 {extra:0.0} mm");
            for (int m = 0; m < 3; m++)
            {
                var pm = Mk(m, extra);
                var prof = SegmentSolver.EndInsulExtraProfileMm(pm, pm.WallMinMm);
                Say($"   形状 {label[m]}：{(prof.Length == 0 ? "（无）" : string.Join("/", prof.Select(v => v.ToString("0.000"))) + " mm")}");
            }
            for (int m = 0; m < 3; m++)
            {
                var rr = SegmentSolver.Solve(Mk(m, extra));
                Say($"   反算电流 {label[m]}：{(rr.Ok ? $"I = {rr.CurrentA:0.0} A　中点 {rr.TMetal[rr.TMetal.Length / 2]:0.00}　两端 {rr.TMetal[0]:0.00}/{rr.TMetal[^1]:0.00}　最高 {rr.TMaxC:0.00}　最低 {rr.TMinC:0.00}" : "失败：" + rr.Message)}");
            }
            foreach (double i in new[] { 1000.0, 1500, 1655, 1800, 2000, 2400, 2746 })
            {
                var parts = new List<string>();
                for (int m = 0; m < 3; m++)
                {
                    var a = SegmentSolver.SolveAtCurrent(Mk(m, extra), i);
                    parts.Add($"{label[m].Trim()} 中点 {a.TMetal[a.TMetal.Length / 2]:0.00} 两端 {a.TMetal[0]:0.00}/{a.TMetal[^1]:0.00}");
                }
                Say($"   定电流 {i:0} A：{string.Join("　", parts)}");
            }
        }
        Say("读法：Bvp1D 把温度夹在 [−273, 2200]；散热表只铺到 设定+400（空管）或 max(设定, 玻璃进口)+400（带玻璃），表外按端点值夹住。"
          + "印出 2200 或负值的行是出了模型范围的垃圾解，不是物理。");
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        File.WriteAllText(NewFile("端部额外保温探针", stamp, "txt"), sb.ToString(), new UTF8Encoding(false));
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
