using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **管强度利用率**（整线判据 ④）—— 逐段算、逐段印、全仓唯一一份。2026-09-18，Opus 5 写。
///
/// ══ 这一份为什么存在（三个「看起来正常的错数」）
///
/// 2026-09-18 旁证查明（出处：`r48_M/deliverable/旁证_2026-09-18/A_安全系数反推与管强度判不了_本次开跑于2026-09-18_005215.md`）：
/// 原来这段判据写在 `LineRunner.Judge` 里，一共 20 行，藏了三个错数 ——
///   (a) 某一段落在拟合区间之外 ⇒ `continue` 跳过，`utilMax` 只在**幸存段**里取最大，
///       却印在「④ 管强度利用率」这一栏上，读起来像整线最大值（实测：0.6887 其实只是 HC1 一段）；
///   (b) 空管到温稳态那张表上，许用侧按空管控温点（升温目标 1150 °C）取，载荷侧却仍带玻璃 ⇒ **混血数**；
///   (c) `Kind = Reference` 写死 `Ok = true` ⇒ 输出里出现「**过**，裕度 −0.401」同行并列。
/// ⇒ 三件都不是「算错了」，是「算出来的东西不是它标签上写的那个东西」。整段提到这里，逐条修，逐条造门。
///
/// ══ 「判不了」的根：段控温点低于拟合下限
///
/// 段控温点默认 1150 / 1080 / 1050 °C（`LineCase.SetpointC`），而纯铂的持久强度拟合区间是
/// **[1100, 1400] °C**（`MaterialDb.Range("Pt", 1100, 1400, true)`）—— 下限 1100 的出处是**工作簿里
/// Tanaka-Pure Pt 的原始数据点只有 1100 / 1200 / 1300 / 1400 四块，没有 1000 °C 那一块**。
/// 于是 HC2（1080）与 HC3（1050）上 <see cref="PtGrade.RuptureStressMPa"/> 返回 NaN。
///
/// **护栏不拆**：五次多项式在区间外发散（实测：纯铂 900 °C 外推得 8648 MPa；1050 °C 得 5.29 MPa，
/// 而 1100 °C 是 2.110 MPa —— 外推把许用值抬高 2.5 倍，**偏危险方向**）。放宽区间去「修好」这条判据，
/// 等于用假数判绿。
///
/// ══ 上界规则（本轮新增；用户 2026-09-18 定）
///
/// 断裂强度 σ_r 随温度**单调下降**（同一寿命）。所以对任何 T ≤ 拟合下限的段：
/// <code>
///     σ_r(T, 寿命)  ≥  σ_r(拟合下限, 寿命)
/// </code>
/// 把**拟合下限温度**的强度当许用值代进去，得到的是利用率的**上界** —— 不外推、不猜、必定偏安全。
///   · 上界 ≤ 1 ⇒ 判**过**（说明里写明「按 1100 °C 保守值，利用率为上界」）；
///   · 上界 &gt; 1 ⇒ 判**不了**，并点名「需补 1000 °C 持久强度数据」——
///     上界越了 1 说明这个保守值已经不够用，真值可能过也可能不过，**不许当过**。
///
/// ⚠⚠ 单调性**不是硬套的，是每次逐 1 K 验过的**（<see cref="MonotoneInFitRange"/>）：
///   实测（2026-09-18，本类同一条式子）：纯铂在 8760 h 下于 [1100, 1400] °C 上逐 1 K 严格单调下降（0 处违反）；
///   但 **Tanaka-ZGS-Pt / Tanaka-ZGS-PtRh10 在 1000 °C 附近不单调**（8760 h 下分别有 28 / 7 处违反），
///   纯铂在 1e5 h 下也有 81 处。⇒ 这条规则只在「这个牌号、这个寿命真的验过单调」时才用；
///   验不过就**判不了并写明**，不许硬套。
///
/// ══ 温度高于拟合上限怎么办：仍然判不了
///
/// 上界规则只对**低于下限**那一侧成立。高于上限时 σ_r(上限) &gt; σ_r(真值)，
/// 拿它当许用值会把利用率**算小**（偏危险方向）—— 所以那一侧不给上界，直接判不了。
///
/// ══ 它仍然是**参考量**，不卡交付
///
/// 用户 2026-08-14：「蠕变、铂金加热蒸发也都不必考虑」⇒ 本项 `Kind = Reference`。
/// 本类不改这个定性，只改「印出来的东西必须是它标签上写的那个东西」。
/// </summary>
public static class TubeStrength
{
    /// <summary>
    /// ★★ 安全系数的**出处注记**（用户 2026-09-18 定；全仓唯一一份写法，参数表说明、报告、HANDOVER 都引它）。
    ///
    /// 反推自供应商工作簿（旁证 A 第 1.2–1.6 节，实测）：`鉑金材料蠕變應力壽命估算.xlsx` 的「使用应力」一列
    /// **就是该牌号、该温度、1000 h 的断裂强度本身**（四行实测 0.991 / 1.001 / 1.003 / 1.013 ⇒ 隐含系数 1.00），
    /// 不是设计许用值。⇒ 不能把工作簿那一列当成「供应商已经留了安全系数」。
    /// </summary>
    public const string SafetyFactorNote =
        "力学安全系数 2.0（用户 2026-09-18 定，留 2.0 不改）。"
      + "出处：供应商工作簿「使用应力」= 1000 h 断裂强度本身（隐含系数 1.00），**不是设计许用值**；"
      + "本 APP 许用 = 设计寿命断裂强度 ÷ 2.0 ≈ 规范 1.5 × 数据离散 1.3。";

    /// <summary>
    /// ★★ 设计寿命的口径注记（用户 2026-09-17：现场换排号直接换新管，1150 °C 服役 ≤ 6 个月）。
    /// 默认仍取 1 年 —— **偏保守**（寿命越长许用应力越低）。全仓唯一一份写法。
    /// </summary>
    public const string LifeNote =
        "设计寿命默认 8760 h（1 年）。现场实际服役 ≤ 6 个月（4380 h，用户 2026-09-17）——"
      + "默认取 1 年**偏保守**（寿命越长断裂强度越低，许用值更小）。";

    /// <summary>单调性验证的步长 K（逐 1 K；写死在这里，门与生产共用一份）。</summary>
    public const double MonotoneStepK = 1.0;

    // ════════════════════════════════════════════════════════════════════
    //  单调性：σ_r 随温度单调下降 —— 上界规则的**前提**，每次现验
    // ════════════════════════════════════════════════════════════════════

    /// <summary>一次单调性验证的结果。<see cref="Ok"/> 为假时 <see cref="Why"/> 说出第一处违反在哪。</summary>
    public sealed class MonotoneOut
    {
        /// <summary>true = 在 [<see cref="FromC"/>, <see cref="ToC"/>] 上逐 <see cref="MonotoneStepK"/> K 严格下降。</summary>
        public bool Ok;
        public double FromC, ToC;
        /// <summary>验了几对相邻点。0 = 一对都没验到（区间无效）⇒ <see cref="Ok"/> 必为假。</summary>
        public int Pairs;
        /// <summary>违反的对数（0 = 全程单调）。</summary>
        public int Violations;
        /// <summary>第一处违反的低温端 °C（没有违反时为 NaN）。</summary>
        public double FirstBadAtC = double.NaN;
        public string Why = "";
    }

    /// <summary>
    /// 在该牌号**自己的拟合区间**上逐 <see cref="MonotoneStepK"/> K 验「温度升 ⇒ 断裂强度降」。
    /// 不外推一格：只验区间内，因为区间外的多项式本来就不可信。
    /// </summary>
    public static MonotoneOut MonotoneInFitRange(PtGrade g, double lifeHours)
    {
        if (g is null) throw new ArgumentNullException(nameof(g));
        var r = new MonotoneOut { FromC = g.CreepTMinC, ToC = g.CreepTMaxC };
        if (!g.HasCreep || double.IsNaN(g.CreepTMinC) || double.IsNaN(g.CreepTMaxC) || g.CreepTMaxC <= g.CreepTMinC)
        {
            r.Why = $"牌号「{g.Name}」没有可用的持久强度拟合区间（{g.CreepTMinC:0}–{g.CreepTMaxC:0} °C）⇒ 单调性无从验起";
            return r;
        }
        if (!(lifeHours > 0))
        {
            r.Why = $"寿命 {lifeHours} h 不是正数 ⇒ 单调性无从验起";
            return r;
        }

        double prev = g.RuptureStressMPa(g.CreepTMinC, lifeHours);
        for (double t = g.CreepTMinC + MonotoneStepK; t <= g.CreepTMaxC + 1e-9; t += MonotoneStepK)
        {
            double cur = g.RuptureStressMPa(Math.Min(t, g.CreepTMaxC), lifeHours);
            r.Pairs++;
            if (!(cur < prev))
            {
                r.Violations++;
                if (double.IsNaN(r.FirstBadAtC)) r.FirstBadAtC = t - MonotoneStepK;
            }
            prev = cur;
        }
        r.Ok = r.Pairs > 0 && r.Violations == 0;
        r.Why = r.Ok
            ? $"牌号「{g.Name}」在 {r.FromC:0}–{r.ToC:0} °C、寿命 {lifeHours:0} h 上逐 {MonotoneStepK:0} K 严格单调下降（验了 {r.Pairs} 对，0 处违反）"
            : r.Pairs == 0
              ? $"牌号「{g.Name}」一对相邻点都没验到 ⇒ 不许认为单调"
              : $"牌号「{g.Name}」在 {r.FromC:0}–{r.ToC:0} °C、寿命 {lifeHours:0} h 上**不单调**："
                + $"{r.Pairs} 对里有 {r.Violations} 处违反，第一处在 {r.FirstBadAtC:0} → {r.FirstBadAtC + MonotoneStepK:0} °C。"
                + "⇒ 「拿拟合下限的强度当保守值」这条推理在这个牌号上**不成立**，不许硬套。";
        return r;
    }

    // ════════════════════════════════════════════════════════════════════
    //  逐段
    // ════════════════════════════════════════════════════════════════════

    /// <summary>一段的管强度账。每一项都要能指回出处：载荷怎么算的、许用值取在哪个温度上、是不是上界。</summary>
    public sealed class SegUtil
    {
        public string Name = "";
        /// <summary>本段控温点 °C（许用值本来该取在这个温度上）。</summary>
        public double SetpointC;
        /// <summary>实际取许用值的温度 °C（= 控温点，或落在区间外时的**拟合下限**）。</summary>
        public double AllowAtC = double.NaN;
        /// <summary>该温度、该寿命下的断裂强度 MPa（= 许用值，安全系数已算在载荷侧）。</summary>
        public double AllowMPa = double.NaN;
        public double VonMisesMPa, ReqMPa;
        /// <summary>利用率 = σ_vm × 安全系数 ÷ 断裂强度。NaN = 算不出。</summary>
        public double Util = double.NaN;
        /// <summary>true = 许用值取在拟合下限上 ⇒ 这个利用率是**上界**（真值只会更小）。</summary>
        public bool UpperBound;
        /// <summary>true = 判不了（判不了不算过）。</summary>
        public bool Undetermined;
        /// <summary>true = 本段载荷按空管算（只有铂管自重，无玻璃液柱与流动压降）。</summary>
        public bool GlassInTube = true;
        public string Why = "";

        /// <summary>报告与界面共用的一行（不许再手抄一份）。</summary>
        public string Show()
            => $"{Name} 控温 {SetpointC:0} °C：σ_vm {VonMisesMPa:0.###} MPa × 安全系数 ⇒ 需 {ReqMPa:0.###} MPa；"
             + (Undetermined || double.IsNaN(Util)
                ? $"**判不了** —— {Why}"
                : $"许用 {AllowMPa:0.###} MPa（取在 {AllowAtC:0} °C）⇒ 利用率 {Util:0.000}"
                  + (UpperBound ? "（**上界**：许用值按拟合下限的保守值取，真值只会更小）" : "")
                  + (Util <= 1.0 + 1e-12 ? "" : " ★ 超 1"))
             + $"；载荷 {(GlassInTube ? "带玻璃（液柱 + 流动压降 + 管内玻璃自重）" : "空管（只有铂管自重，无玻璃液柱与流动压降）")}";
    }

    /// <summary>
    /// 第 i 段判 ④ 时用的那块板：与 <c>LineRunner.Judge</c> 原来的取法逐字相同（提到这里，两边不各抄一份）。
    /// ⚠ 板只为凑 <see cref="Mechanics.Check"/> 的签名（那里要算舌片那几项）—— ④ 只读管，法兰不承重（§4.2d）。
    /// </summary>
    public static FlangePlate PlateFor(LineCase c, int i)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        return c.FlangePlates.Length > 0 ? c.FlangePlates[Math.Min(i, c.FlangePlates.Length - 1)]
             : c.GeomForJudgeAt(i) ?? Mechanics.TubeOnlyPlate;
    }

    /// <summary>
    /// 算**一段**的管强度账（上界规则就在这里，不在调用方）。
    /// </summary>
    /// <param name="p">本段实际用的参数（<c>LineRunner.BaseSegParams</c> 造的那一份）。</param>
    /// <param name="wallMm">管壁厚 mm（算例的，不是参数表的）。</param>
    /// <param name="plate">见 <see cref="PlateFor"/>。</param>
    /// <param name="setpointC">本段控温点 °C。</param>
    /// <param name="name">段名（报告里要点名到段）。</param>
    /// <param name="glassInTube">管里有没有玻璃（空管态为 false，见 <see cref="Mechanics.Check"/>）。</param>
    public static SegUtil ForSegment(DesignInputs p, double wallMm, FlangePlate plate,
                                     double setpointC, string name, bool glassInTube)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));
        var u = new SegUtil { Name = name, SetpointC = setpointC, GlassInTube = glassInTube };

        var mr = Mechanics.Check(p, wallMm, 2.0, plate, glassInTube: glassInTube);
        u.VonMisesMPa = mr.TubeVonMisesMPa;
        u.ReqMPa = mr.TubeReqAllowMPa;

        var g = MaterialDb.Get(p.GradeName);
        double life = p.DesignLifeHours;

        // ① 控温点就在拟合区间里 ⇒ 直接取，没有上界一说
        if (g.InCreepRange(setpointC))
        {
            u.AllowAtC = setpointC;
            u.AllowMPa = g.AllowableMPa(setpointC, life, 1.0);
        }
        // ② 控温点**高于**拟合上限 ⇒ 上界规则反向（σ_r(上限) 偏高 = 偏危险），不给数
        else if (g.HasCreep && setpointC > g.CreepTMaxC)
        {
            u.Undetermined = true;
            u.Why = $"{setpointC:0} °C **高于**「{p.GradeName}」持久强度实测区间 [{g.CreepTMinC:0}, {g.CreepTMaxC:0}] °C 的上限。"
                  + "上界规则只对低于下限那一侧成立：温度更高时断裂强度更低，拿拟合上限的强度当许用值会把利用率算小（**偏危险方向**）"
                  + $"⇒ 不给数。【下一步】补该牌号 {setpointC:0} °C 以上的持久强度数据。";
            return u;
        }
        // ③ 没有蠕变数据
        else if (!g.HasCreep)
        {
            u.Undetermined = true;
            u.Why = $"牌号「{p.GradeName}」没有持久强度数据 ⇒ 许用值无从取。";
            return u;
        }
        // ④ 控温点**低于**拟合下限 ⇒ 上界规则（先验单调，再取拟合下限的保守值）
        else
        {
            var mono = MonotoneInFitRange(g, life);
            if (!mono.Ok)
            {
                u.Undetermined = true;
                u.Why = $"{setpointC:0} °C 低于「{p.GradeName}」持久强度实测区间 [{g.CreepTMinC:0}, {g.CreepTMaxC:0}] °C 的下限，"
                      + $"而保守上界这条路也走不通：{mono.Why}";
                return u;
            }
            u.UpperBound = true;
            u.AllowAtC = g.CreepTMinC;
            u.AllowMPa = g.AllowableMPa(g.CreepTMinC, life, 1.0);
        }

        if (!(u.AllowMPa > 0) || double.IsNaN(u.AllowMPa))
        {
            u.Undetermined = true;
            u.AllowAtC = double.NaN;
            u.Why = $"「{p.GradeName}」在 {setpointC:0} °C、寿命 {life:0} h 下的断裂强度算不出来（{u.AllowMPa}）。";
            return u;
        }

        u.Util = u.ReqMPa / u.AllowMPa;

        // 上界越了 1 ⇒ 保守值已经不够用，真值可能过也可能不过 ⇒ **判不了**，不许当过
        if (u.UpperBound && u.Util > 1.0 + 1e-12)
        {
            u.Undetermined = true;
            u.Why = $"{setpointC:0} °C 低于「{p.GradeName}」持久强度实测区间 [{g.CreepTMinC:0}, {g.CreepTMaxC:0}] °C 的下限；"
                  + $"按拟合下限 {g.CreepTMinC:0} °C 的保守值算出来的**上界利用率 {u.Util:0.000} 已经超过 1** ——"
                  + "保守值不够用了，真值可能过也可能不过 ⇒ **判不了，不许当过**。"
                  + $"【下一步】需补「{p.GradeName}」**1000 °C** 那一块持久强度数据"
                  + "（工作簿里 Tanaka-Pure Pt 的原始点只有 1100 / 1200 / 1300 / 1400 四个温度），重拟系数后区间下延。";
        }
        return u;
    }

    // ════════════════════════════════════════════════════════════════════
    //  整线
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 逐段算完之后的整线一条判据。
    /// **整线值 = 逐段最大**（含上界那些段）；**任何一段判不了 ⇒ 整线判不了**（判不了不算过）。
    /// ⚠ 原来的写法是「NaN 段 `continue` 跳过，剩下的取最大」—— 那个数印在整线栏上，
    ///   读起来像整线最大值，其实只是幸存段的最大值。不许再回去。
    /// </summary>
    public static ConstraintOut Judge(LineCase c, DesignInputs[] segParams, string[] segNames, double[] setpointC)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        var segs = Rows(c, segParams, segNames, setpointC);
        return Judge(c, segs);
    }

    /// <summary>逐段账（调用方已经有 <see cref="SegUtil"/> 时用这一支；报告要逐段印就拿它）。</summary>
    public static SegUtil[] Rows(LineCase c, DesignInputs[] segParams, string[] segNames, double[] setpointC)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        if (segParams is null) throw new ArgumentNullException(nameof(segParams));
        int n = segParams.Length;
        var rows = new SegUtil[n];
        for (int i = 0; i < n; i++)
            rows[i] = ForSegment(segParams[i], c.WallMm, PlateFor(c, i),
                                 i < setpointC.Length ? setpointC[i] : double.NaN,
                                 i < segNames.Length ? segNames[i] : $"段{i + 1}",
                                 !c.EmptyTube);
        return rows;
    }

    /// <summary>逐段账 ⇒ 整线那一条。</summary>
    public static ConstraintOut Judge(LineCase c, IReadOnlyList<SegUtil> rows)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        var undet = rows.Where(r => r.Undetermined).ToArray();
        var numbered = rows.Where(r => !double.IsNaN(r.Util)).ToArray();
        var worst = numbered.Length == 0 ? null : numbered.OrderByDescending(r => r.Util).First();
        bool anyBound = numbered.Any(r => r.UpperBound);
        bool blind = undet.Length > 0 || rows.Count == 0;

        string head = "参考（用户 2026-08-14：蠕变不必考虑，本项降级，不卡交付）；"
                    + "法兰不承重（氧化铝管托底，§4.2d），故不校核舌片。"
                    + $"　利用率 = σ_vm × 安全系数 ÷ 断裂强度(取值温度, 设计寿命)。{SafetyFactorNote}　{LifeNote}　";
        string perSeg = rows.Count == 0 ? "（一段都没有）"
                      : "逐段：" + string.Join("；　", rows.Select(r => r.Show())) + "。";
        string lineWord = blind
            ? $"★ **整线判不了**：{undet.Length} 段（{string.Join("、", undet.Select(r => r.Name))}）判不了 ——"
              + "**任何一段判不了，整线就判不了**；印一个「幸存段的最大值」当整线值，读的人会以为那是整线最大。"
            : $"整线 = 逐段最大 = {worst!.Util:0.000}（{worst.Name}）"
              + (anyBound ? "　⚠ 其中有段的利用率是**上界**（许用值按拟合下限的保守值取）——"
                          + "上界 ≤ 1 就是过，真值只会更小。" : "");

        return new ConstraintOut
        {
            Name = "④ 管强度利用率",
            Unit = "—",
            Kind = CheckKind.Reference,
            Limit = 1.0,
            LessIsBetter = true,
            Actual = worst?.Util ?? double.NaN,
            Undetermined = blind,
            // 参考量的 Ok 不再写死 true：它照实说「这个数有没有越 1」。
            // 显示层对参考量不印「过／不过」（Criteria.Verdict），所以这一位只进逻辑，不进判词。
            Ok = !blind && worst is not null && worst.Util <= 1.0 + 1e-12,
            Where = blind ? string.Join("、", undet.Select(r => r.Name)) + " 判不了"
                  : worst is null ? "—" : worst.Name + (worst.UpperBound ? "（上界）" : ""),
            Note = head + lineWord + "　" + perSeg,
        };
    }
}
