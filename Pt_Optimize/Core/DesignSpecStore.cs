using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PtOptimize.Core;

/// <summary>
/// 设计记录的**文件形态**：读写 <c>finaldesigns/*.fd.json</c>。
///
/// ★ 为什么要有它（用户 2026-08-22：「若以读档的形式呢？不想让工程师复制粘贴，
///   感觉不靠谱」）：
///
///   在此之前，把一个解落成设计记录要**人工把十几个数抄进 DesignSpec.cs**，
///   其中五个判据值还得从判据表里逐个读。抄错一位要跑 8 分钟 --selfcheck 才知道。
///   「同一个数存两处然后悄悄漂开」是本项目栽得最多的一类 —— 手抄正是它的入口。
///
/// ★ 这**不削弱** --selfcheck 的作用。A 段验的从来不是「这个设计对不对」，
///   而是「内核以后改了，还能不能算出同一个数」——即回归。那五个记录值本来就
///   来自同一次运行（人抄的），改成程序写，只是把纯风险环节拿掉。
///   真正提供保障的是**档在 git 里、变更走 diff**，而不是它是 .cs 还是 .json。
///
/// ⚠ 内置的四个常数（<see cref="DesignSpec.Builtin"/>）**一个都不动**：
///   它们是守内核的回归基准，继续写死在代码里。文件档是工程师日常产出的新方案，
///   两者都进 <see cref="DesignSpec.All"/>、都受 A 段回归保护。
/// </summary>
public static class DesignSpecStore
{
    public const string DirName = "finaldesigns";
    public const string Ext = ".fd.json";

    /// <summary>
    /// 读档时出的岔子。**必须一条条报出来，不许静默跳过** ——
    /// 少一个档就是少一组判据，而「判据凭空消失」正是铁律二盯的那一族
    /// （曾有判据写成「有数据才加入检查表」，数据缺失时整条判据消失，
    ///   实测增量降 +32 K 却报「全过」）。
    /// </summary>
    public static readonly List<string> LoadErrors = new();

    private sealed class Dto
    {
        public string? name { get; set; }
        public string? provenance { get; set; }
        public string? binding { get; set; }
        public string? invalid { get; set; }
        public string[]? invalidChecks { get; set; }
        public double? wallMm { get; set; }
        public double? tubeInsulMm { get; set; }
        public double? tubeIdMm { get; set; }            // R30：管内径进几何（旧档没有 = 50）
        public bool? tabTaper { get; set; }              // R31：锥形舌片
        public double?[]? tabHoleRotDeg { get; set; }    // R31：舌孔朝向
        /// <summary>设计电流密度 J（用户 2026-09-09：工程师设定）。旧档没有 ⇒ 预设 10。</summary>
        public double? jDesignAPerMm2 { get; set; }
        public double? discRadiusMm { get; set; }
        public double? tabLengthMm { get; set; }
        public double? tabHalfWidthMm { get; set; }
        public double? clampTempC { get; set; }
        // ⚠ 下面这些**都是 BuildCase 会读的几何**。漏一个，档就造出**另一个零件**——
        //   2026-08-23 第一版正是漏了它们：试档算出 ③=132.9 而记录是 5.182。
        //   （selfcheck A 段当场抓住了 —— 但别指望下次也这么幸运，
        //     真正的守卫是下面 Tests 里那条「存→读往返必须一模一样」。）
        //   （RingRadiiMm / HoleRadiusMm 是**算出来的**只读属性，由 WallMm 与 RingWidthMm 导出
        //     ⇒ 不进档：存一份导出量，就是给它开一个与本源打架的机会。）
        public double? tabFilletMm { get; set; }
        public double? ringWidthMm { get; set; }
        public double? flangeInsulMm { get; set; }
        public bool? flangeInsulated { get; set; }
        public double? clampLengthMm { get; set; }
        public double[]? setpointC { get; set; }
        /// <summary>每段直接加热铂金管的长度 mm（逐段，用户 2026-09-03）。缺省 ⇒ 按段数铺默认值。</summary>
        public double[]? segLengthMm { get; set; }
        /// <summary>板厚 mm 逐片。★ R47 第三轮 N5：图纸档写 null（NaN），k 在 <see cref="thicknessScale"/> 里；解析档照旧是数。</summary>
        public double?[]? tabThickMm { get; set; }
        /// <summary>★ R47 第三轮 N5：图纸路径逐片厚度倍数 k（图纸整体 ×k）。解析档不写。</summary>
        public double[]? thicknessScale { get; set; }
        /// <summary>
        /// ★ R47 B（2026-09-13）：逐片舌保温。**读**时接受两种形态：数组（逐片，现行）或**单个数**（旧档／图纸路径
        /// 早期只有一个标量 `_tabIns3dm`）—— 单个数读回填成所有片同值，并写进 DesignSpec.Notes 说明。写时一律写数组。
        /// 用 JsonElement 是为了不改 JSON 形态就两种都认得；旧键 tabInsul3dmMm（标量）同样认。
        /// </summary>
        public JsonElement? tabInsulMm { get; set; }
        public double? tabInsul3dmMm { get; set; }
        /// <summary>★ R47 B：几何来源（解析／图纸 .3dm）与图纸路径逐片 .3dm 文件名。旧档没有 ⇒ 空。</summary>
        public string? geomSource { get; set; }
        public string[]? flangeFile3dm { get; set; }
        public double[]? ringMul { get; set; }
        // ★★★★★ A3（2026-09-02）：**这三个 2026-08-30 就成了设计的一部分，而档一直没存**。
        //   ringMul2（t₂）尤其要命 —— 它是求解器治「管孔净流入」的**首选**旋钮
        //   （每克铂买到的裕度是板厚的 1.7–3.3 倍，排在板厚前面）。
        //   ⇒ 求解器解出一个含 t₂ 的设计 → 点存档 → t₂ 丢掉 →
        //     下次载入算出来的是**另一个设计**。「储存计算结果」存的不是那个结果。
        //   ⚠ 上面那条警告（「漏一个，档就造出另一个零件」）说的正是这件事，
        //     而它没拦住 —— 因为拦它的那条测试的**样本**（Distinctive）停在 08-23，
        //     新加的字段本来就等于默认值，漏读照样相等。样本烂了，门就空转。
        //   ⚠ 类型是 double?[]：这三项用 **NaN 当哨兵**（= 该片不逐片自定），
        //     而 JSON 写不了 NaN。逐片映射 NaN ↔ null —— 部分设定（只有某片有 t₂）也存得准。
        //     给整个 Opt 开 AllowNamedFloatingPointLiterals 会往档里写 "NaN" 字符串，不干净。
        public double?[]? ringW1Mm { get; set; }
        public double?[]? ringW2Mm { get; set; }
        public double?[]? ringMul2 { get; set; }
        /// <summary>★ 舌片厚（逐片，R11 2026-09-08：= I/(J·舌宽) 闭式）。NaN ↔ null（= 与基板同厚，旧档）。</summary>
        public double?[]? tongueThickMm { get; set; }
        public double?[]? tabArmX0Mm { get; set; }       // R29：舌根加厚带（派生）
        public double?[]? tabArmX1Mm { get; set; }
        public double?[]? tabArmThickMm { get; set; }

        /// <summary>
        /// ★★★ 圆盘背侧减重槽的张角（逐片，度；0 = 不开槽）。2026-09-05 加。
        /// 不存它的后果与 ringMul2 那次一样：**存下来的档少了一根旋钮**，
        /// 复算时槽消失、抽热多出四成，而判据表照样出数、看不出异样。
        /// </summary>
        public double[]? slotSpanDeg { get; set; }
        public double? slotRInMm { get; set; }

        /// <summary>舌板开孔孔径（逐片，半径 mm；0 = 无孔）与孔心位置。2026-09-05 加（用户要求 R5）。</summary>
        public double[]? tabHoleRMm { get; set; }
        /// <summary>孔的顺流拉长比（逐片；1 = 圆）。2026-09-05。</summary>
        public double[]? tabHoleAspect { get; set; }
        /// <summary>旧档（2026-09-09 之前）的孔心：整线一个数。只读不写 —— 读进来铺到每一片。</summary>
        public double? tabHoleXMm { get; set; }
        /// <summary>★ R12：孔心逐片（场定）。NaN ↔ null（= 默认规则：自由段中点）。</summary>
        public double?[]? tabHoleXByPlateMm { get; set; }
        /// <summary>★ R12：圆盘槽槽心角（逐片，场定）。NaN ↔ null（= 默认 0°）。</summary>
        public double?[]? slotCenterDeg { get; set; }
        /// <summary>★ R13：舌孔形状族（逐片：0 圆／椭圆、3 圆角三角、4 圆角方）。</summary>
        public double[]? tabHoleSides { get; set; }
        /// <summary>★ R13：圆盘挖料形状族（逐片：0 弯椭圆槽、1 长椭圆）。</summary>
        public double[]? discCutShape { get; set; }
        /// <summary>★ R13：长椭圆长轴方向 °（逐片，场定）。NaN ↔ null（= 切向）。</summary>
        public double?[]? discCutRotDeg { get; set; }
        public double? slotROutMm { get; set; }
        public double? totalMassG { get; set; }
        public double? tubeMassG { get; set; }
        public double? flangeMassG { get; set; }
        public double? residualK { get; set; }
        public Checks? checks { get; set; }

        /// <summary>
        /// ★★★★★ A2（2026-09-02 用户拍板）：**判据值要带口径**。
        ///
        /// checks 里那五个数是存的，但**没说它们是在哪张网格上算的**。而同一个设计：
        /// <code>
        ///   粗网格（导航 2 mm）      法兰增量温降  4.720 K   看着余量 53 %
        ///   加密复算到数不再变        法兰增量温降 10.329 K   越限
        /// </code>
        /// 同一个字段名，差的就是这个口径。不存它，档自己说不清拿的是哪一个 ——
        /// 而现役两档的记录值正是这么来的。
        /// </summary>
        public Verified? verified { get; set; }

        public sealed class Verified
        {
            public double? meshMm { get; set; }
            public double? flangeDipK { get; set; }
            public double? holeFluxW { get; set; }
            public double? discOverK { get; set; }
            public string? note { get; set; }
        }

        public sealed class Checks
        {
            public double? rampH { get; set; }
            public double? discOverK { get; set; }
            public double? holeFluxW { get; set; }
            public double? flangeDipK { get; set; }
            public double? tubeJ { get; set; }
        }
    }

    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 从 <c>AppContext.BaseDirectory</c> 逐级向上找 <c>finaldesigns/</c>。
    /// 与 <see cref="UI.ManualPage"/> 找 <c>docs/</c> 是同一套做法 ——
    /// APP 从 <c>bin/Release/…</c> 跑，而档要在仓库里。
    /// </summary>
    public static string? FindDir(bool create = false)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d is not null; i++, d = d.Parent)
        {
            string p = Path.Combine(d.FullName, DirName);
            if (Directory.Exists(p)) return p;
            // 建目录时认「仓库根」：有 .git 的那一层。别在 bin 下面随手造一个 ——
            // 那儿的东西不进 git，第一层保障（变更被 diff 记录）就没了。
            // ★ 2026-09-09（提交本任务时撞到）：.git **不总是目录** —— `git worktree` checkout 出来的
            //   工作树里 .git 是一个指向主仓库 gitdir 的**文件**（内容一行 `gitdir: ...`）。
            //   原来只认 Directory.Exists ⇒ 在工作树里跑 UiWiring 自检必炸「找不到仓库根」，
            //   而这个自检正是 pre-commit 钩子要求的一环。两种都是「在仓库里」的合法标志，都认。
            if (create && (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))))
                return Directory.CreateDirectory(p).FullName;
        }
        return null;
    }

    /// <summary>
    /// 读目录里所有档。<paramref name="builtinNames"/> 用来查重名。
    ///
    /// ⚠ 出错一律记进 <see cref="LoadErrors"/> 并**跳过那一个**，
    ///   但绝不静默 —— 调用方（启动路径与 --selfcheck）都会把它当失败报出来。
    /// </summary>
    public static List<DesignSpec> LoadAll(IEnumerable<string> builtinNames)
    {
        LoadErrors.Clear();
        var outp = new List<DesignSpec>();
        string? dir = FindDir();
        if (dir is null) return outp;              // 没有目录 = 没有文件档，不是错

        var seen = new HashSet<string>(builtinNames, StringComparer.Ordinal);
        foreach (string f in Directory.GetFiles(dir, "*" + Ext).OrderBy(x => x, StringComparer.Ordinal))
        {
            DesignSpec? fd;
            try { fd = Parse(File.ReadAllText(f), Path.GetFileName(f)); }
            catch (Exception ex) { LoadErrors.Add($"{Path.GetFileName(f)}：{ex.Message}"); continue; }
            if (fd is null) continue;              // Parse 已记过错

            // ⚠ 重名不许静默覆盖：下拉里两个同名档，选中哪个全凭顺序，
            //   而两者的几何可能完全不同 —— 那是「拿另一个设计的数出图」的入口。
            if (!seen.Add(fd.Name))
            {
                LoadErrors.Add($"{Path.GetFileName(f)}：档名「{fd.Name}」与已有档重名 —— 已拒绝载入");
                continue;
            }
            outp.Add(fd);
        }
        return outp;
    }

    /// <summary>NaN 存成 null —— 「没这个数」与「这个数是 0」必须分得开。</summary>
    private static double? Nz(double v) => double.IsNaN(v) ? null : v;

    /// <summary>整组 NaN ⇒ 整项不写（省得档里一排 null）；否则逐片 NaN ↔ null。</summary>
    private static double?[]? NzA(double[] a) =>
        a is null || a.All(double.IsNaN) ? null : a.Select(Nz).ToArray();

    private static double[] NaA(double?[] a) => a.Select(x => x ?? double.NaN).ToArray();

    private static DesignSpec? Parse(string json, string file)
    {
        var d = JsonSerializer.Deserialize<Dto>(json, Opt)
                ?? throw new InvalidDataException("解析出空对象");

        // 必填项缺一不可。缺了就**拒绝**这个档，不拿默认值凑 ——
        // 默认值凑出来的是一个「看起来正常」的错设计。
        string Need(string? v, string k) =>
            string.IsNullOrWhiteSpace(v) ? throw new InvalidDataException($"缺 {k}") : v;
        double NeedD(double? v, string k) => v ?? throw new InvalidDataException($"缺 {k}");
        double[] NeedA(double[]? v, string k, int n) =>
            v is null ? throw new InvalidDataException($"缺 {k}")
            : v.Length != n ? throw new InvalidDataException($"{k} 应有 {n} 个，实为 {v.Length} 个")
            : v;
        double?[] NeedN(double?[]? v, string k, int n) =>
            v is null ? throw new InvalidDataException($"缺 {k}")
            : v.Length != n ? throw new InvalidDataException($"{k} 应有 {n} 个，实为 {v.Length} 个")
            : v;

        var c = d.checks ?? throw new InvalidDataException("缺 checks（五个判据记录值）—— 没有它就无法回归对账");

        // 片数 = 段数 + 1。setpointC 缺省时按 DesignSpec 的默认段数（3 段 ⇒ 4 片）。
        int nPlate = (d.setpointC is { Length: > 0 } sp ? sp.Length : new DesignSpec().SetpointC.Length) + 1;

        var fd = new DesignSpec
        {
            Name = Need(d.name, "name"),
            Provenance = Need(d.provenance, "provenance"),
            Binding = d.binding ?? "",
            Invalid = d.invalid ?? "",
            InvalidChecks = d.invalidChecks ?? Array.Empty<string>(),
            WallMm = NeedD(d.wallMm, "wallMm"),
            TubeInsulMm = d.tubeInsulMm ?? 10.0,
            TubeIdMm = d.tubeIdMm ?? 50.0,
            TabTaper = d.tabTaper ?? false,
            JDesignAPerMm2 = d.jDesignAPerMm2 ?? SectionSizing.JDesignAPerMm2,
            DiscRadiusMm = NeedD(d.discRadiusMm, "discRadiusMm"),
            TabLengthMm = NeedD(d.tabLengthMm, "tabLengthMm"),
            TabHalfWidthMm = NeedD(d.tabHalfWidthMm, "tabHalfWidthMm"),
            // ★★★ 2026-09-02：片数**由段数决定**（n 段 → n+1 片），不再写死 4。
            //   段数可调是硬要求；写死 4 会让「4 段的档」当场被拒或悄悄少一片。
            TabThickMm = NaA(NeedN(d.tabThickMm, "tabThickMm", nPlate)),   // R47 第三轮 N5：图纸档逐片 null ⇒ NaN
            ThicknessScale = d.thicknessScale is { Length: > 0 } ks ? (ks.Length == nPlate ? (double[])ks.Clone() : throw new InvalidDataException($"thicknessScale 应有 {nPlate} 个，实为 {ks.Length} 个")) : Array.Empty<double>(),
            TabInsulMm = TabInsulFrom(d, nPlate, out string tabInsulNote),
            RingMul = NeedA(d.ringMul, "ringMul", nPlate),
            // R47 B：几何来源／图纸文件名／读档说明
            GeomSource = d.geomSource ?? "",
            FlangeFile3dm = d.flangeFile3dm ?? Array.Empty<string>(),
            // R47 复修 M9：图纸路径出的档，读回就说清楚怎么复现（几何来源决定，不看别的）
            Notes = tabInsulNote + ((d.geomSource ?? "") == DesignSpec.GeomSourceDrawing ? (tabInsulNote.Length > 0 ? "　" : "") + DesignSpec.DrawingRecordNote : ""),
            TotalMassG = NeedD(d.totalMassG, "totalMassG"),
            TubeMassG = d.tubeMassG ?? 0,
            FlangeMassG = d.flangeMassG ?? 0,
            ResidualK = d.residualK ?? 0.5,
            RampH = NeedD(c.rampH, "checks.rampH"),
            DiscOverK = NeedD(c.discOverK, "checks.discOverK"),
            HoleFluxW = NeedD(c.holeFluxW, "checks.holeFluxW"),
            FlangeDipK = NeedD(c.flangeDipK, "checks.flangeDipK"),
            TubeJ = NeedD(c.tubeJ, "checks.tubeJ"),
            FromFile = file,
        };
        if (d.setpointC is { Length: > 0 }) fd.SetpointC = d.setpointC;
        if (d.segLengthMm is { Length: > 0 }) fd.SegLengthMm = d.segLengthMm;
        if (d.clampTempC is { } ct) fd.ClampTempC = ct;
        if (d.tabFilletMm is { } tf) fd.TabFilletMm = tf;
        if (d.ringWidthMm is { } rw) fd.RingWidthMm = rw;
        if (d.flangeInsulMm is { } fi) fd.FlangeInsulMm = fi;
        if (d.flangeInsulated is { } fe) fd.FlangeInsulated = fe;
        if (d.clampLengthMm is { } cl) fd.ClampLengthMm = cl;
        // A3：渐变环那三个（缺省即 NaN 哨兵 = 不逐片自定，与 DesignSpec 的默认一致）
        if (d.slotSpanDeg is { Length: > 0 } slotArr) fd.SlotSpanDeg = (double[])slotArr.Clone();
        if (d.slotRInMm is { } sri) fd.SlotRInMm = sri;
        if (d.tabHoleRMm is { Length: > 0 } holeArr) fd.TabHoleRMm = (double[])holeArr.Clone();
        if (d.tabHoleAspect is { Length: > 0 } aspArr) fd.TabHoleAspect = (double[])aspArr.Clone();
        // ★ R12（2026-09-09）：孔心改逐片。旧档只有一个标量 ⇒ 铺到每一片（与从前「整线一个数」逐位同义）；新档逐片数组优先。
        if (d.tabHoleXMm is { } thx) fd.TabHoleXMm = Enumerable.Repeat(thx, nPlate).ToArray();
        if (d.tabHoleXByPlateMm is { Length: > 0 } txp) fd.TabHoleXMm = NaA(txp);
        if (d.slotCenterDeg is { Length: > 0 } scd) fd.SlotCenterDeg = NaA(scd);
        if (d.tabHoleSides is { Length: > 0 } ths) fd.TabHoleSides = (double[])ths.Clone();
        if (d.discCutShape is { Length: > 0 } dcs) fd.DiscCutShape = (double[])dcs.Clone();
        if (d.discCutRotDeg is { Length: > 0 } dcr) fd.DiscCutRotDeg = NaA(dcr);
        if (d.slotROutMm is { } sro) fd.SlotROutMm = sro;
        if (d.ringW1Mm is { Length: > 0 } r1) fd.RingW1Mm = NaA(r1);
        if (d.ringW2Mm is { Length: > 0 } r2) fd.RingW2Mm = NaA(r2);
        if (d.ringMul2 is { Length: > 0 } t2) fd.RingMul2 = NaA(t2);
        if (d.tongueThickMm is { Length: > 0 } tg) fd.TongueThickMm = NaA(tg);
        if (d.tabArmX0Mm is { Length: > 0 } ax0) fd.TabArmX0Mm = NaA(ax0);          // R29
        if (d.tabHoleRotDeg is { Length: > 0 } hr) fd.TabHoleRotDeg = NaA(hr);      // R31
        if (d.tabArmX1Mm is { Length: > 0 } ax1) fd.TabArmX1Mm = NaA(ax1);
        if (d.tabArmThickMm is { Length: > 0 } axt) fd.TabArmThickMm = NaA(axt);
        // ★ R47 第三轮 N5：R47 第二轮之前写出的图纸档把厚度倍数 k 记在 tabThickMm 栏里（当毫米）。
        //   读回时按 k 接过来、板厚栏改 NaN，并在 Notes 里说出来 —— 不许把 k 当一片 1 mm 的解析板。
        if (fd.IsDrawingRecord && fd.ThicknessScale.Length == 0 && fd.TabThickMm.Any(v => !double.IsNaN(v)))
        {
            fd.ThicknessScale = fd.TabThickMm.Select(v => double.IsNaN(v) ? 1.0 : v).ToArray();
            fd.TabThickMm = Enumerable.Repeat(double.NaN, fd.TabThickMm.Length).ToArray();
            fd.Notes += (fd.Notes.Length > 0 ? "　" : "") + "旧图纸档把厚度倍数 k 记在板厚栏里，已按 k 读回（板厚栏不适用于图纸档）。";
        }
        // ★ 最后统一按段数对齐 —— 档里存的片数与 setpointC 对不上时（旧档、手改过的档），
        //   这里补齐/裁掉，而不是让它带着一个错长度进计算。
        fd.Fit();
        // A2：口径。没有这一段就是「没做过加密复算」，保持 NaN。
        if (d.verified is { } v && v.meshMm is { } mm)
        {
            fd.VerifiedMeshMm = mm;
            fd.VerifiedFlangeDipK = v.flangeDipK ?? double.NaN;
            fd.VerifiedHoleFluxW  = v.holeFluxW  ?? double.NaN;
            fd.VerifiedDiscOverK  = v.discOverK  ?? double.NaN;
            fd.VerifiedNote = v.note ?? "";
        }
        return fd;
    }

    /// <summary>
    /// ★ R47 B（2026-09-13）：逐片舌保温**向后兼容**读法。
    /// 数组 ⇒ 按片数校验（与从前一样，缺／长度不对都拒）；**单个数**（旧档，或旧键 tabInsul3dmMm）⇒ 填成所有片同值，
    /// 并把这件事写进 <paramref name="note"/>（DesignSpec.Notes，界面载入时印出来）—— 不许静默补。
    /// </summary>
    private static double[] TabInsulFrom(Dto d, int nPlate, out string note)
    {
        note = "";
        var el = d.tabInsulMm;
        if (el is { } e && e.ValueKind == JsonValueKind.Array)
        {
            var arr = e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble()
                                                     : throw new InvalidDataException("tabInsulMm 里有不是数的项")).ToArray();
            if (arr.Length != nPlate) throw new InvalidDataException($"tabInsulMm 应有 {nPlate} 个，实为 {arr.Length} 个");
            return arr;
        }
        double? one = el is { } e1 && e1.ValueKind == JsonValueKind.Number ? e1.GetDouble() : d.tabInsul3dmMm;
        if (one is { } v)
        {
            note = $"旧档只记了一个舌保温 {v:0.###} mm（不分片），读档时已填成所有 {nPlate} 片同值；另存一次就会按逐片写出。";
            return Enumerable.Repeat(v, nPlate).ToArray();
        }
        throw new InvalidDataException("缺 tabInsulMm");
    }

    /// <summary>把一个设计记录写成档。返回写出的路径。**不覆盖已存在的同名文件。**</summary>
    public static string Save(DesignSpec fd)
    {
        string dir = FindDir(create: true)
                     ?? throw new IOException($"找不到仓库根（没有 .git），无法建 {DirName}/");
        string safe = string.Concat(fd.Name.Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        string path = Path.Combine(dir, safe + Ext);
        // ⚠ 不覆盖：档是回归基准，悄悄换掉一份基准比没有基准更坏。
        if (File.Exists(path))
            throw new IOException($"已存在同名档：{Path.GetFileName(path)}　—— 请改名，或先把旧档删掉并说明为什么");

        var dto = new Dto
        {
            name = fd.Name,
            provenance = fd.Provenance,
            binding = fd.Binding,
            invalid = fd.Invalid.Length > 0 ? fd.Invalid : null,
            invalidChecks = fd.InvalidChecks.Length > 0 ? fd.InvalidChecks : null,
            wallMm = fd.WallMm,
            tubeInsulMm = fd.TubeInsulMm,
            tubeIdMm = fd.TubeIdMm,
            tabTaper = fd.TabTaper ? true : null,
            tabHoleRotDeg = NzA(fd.TabHoleRotDeg),
            jDesignAPerMm2 = fd.JDesignAPerMm2,
            discRadiusMm = fd.DiscRadiusMm,
            tabLengthMm = fd.TabLengthMm,
            tabHalfWidthMm = fd.TabHalfWidthMm,
            clampTempC = fd.ClampTempC,
            setpointC = fd.SetpointC,
            segLengthMm = fd.SegLengthMm,
            tabFilletMm = fd.TabFilletMm,
            ringWidthMm = fd.RingWidthMm,
            flangeInsulMm = fd.FlangeInsulMm,
            flangeInsulated = fd.FlangeInsulated,
            clampLengthMm = fd.ClampLengthMm,
            tabThickMm = fd.TabThickMm.Select(Nz).ToArray(),                 // R47 第三轮 N5：图纸档的 NaN 存成 null
            thicknessScale = fd.ThicknessScale.Length > 0 ? fd.ThicknessScale : null,   // 图纸档逐片 k
            tabInsulMm = JsonSerializer.SerializeToElement(fd.TabInsulMm),   // 逐片数组（读时也认旧档的单个数，见 TabInsulFrom）
            geomSource = fd.GeomSource.Length > 0 ? fd.GeomSource : null,      // R47 B：几何来源／图纸文件名
            flangeFile3dm = fd.FlangeFile3dm.Length > 0 ? fd.FlangeFile3dm : null,
            ringMul = fd.RingMul,
            ringW1Mm = NzA(fd.RingW1Mm),
            ringW2Mm = NzA(fd.RingW2Mm),
            ringMul2 = NzA(fd.RingMul2),
            tongueThickMm = NzA(fd.TongueThickMm),
            tabArmX0Mm = NzA(fd.TabArmX0Mm), tabArmX1Mm = NzA(fd.TabArmX1Mm), tabArmThickMm = NzA(fd.TabArmThickMm),   // R29
            slotSpanDeg = fd.SlotSpanDeg.Any(v => v > 0.5) ? fd.SlotSpanDeg : null,
            slotRInMm = double.IsNaN(fd.SlotRInMm) ? null : fd.SlotRInMm,
            tabHoleRMm = fd.TabHoleRMm.Any(v => v > 0.05) ? fd.TabHoleRMm : null,
            tabHoleAspect = fd.TabHoleAspect.Any(v => Math.Abs(v - 1.0) > 1e-9) ? fd.TabHoleAspect : null,
            // ★ R12/R13（2026-09-09）：孔心逐片（不再写标量）、槽心角、两个形状族、长椭圆方向。
            //   与 ringMul2 那次同一条教训：少存一个，载入后就是**另一个设计**。样本门 Distinctive 已覆盖它们。
            tabHoleXByPlateMm = NzA(fd.TabHoleXMm),
            slotCenterDeg = NzA(fd.SlotCenterDeg),
            tabHoleSides = fd.TabHoleSides.Any(v => v > 0.5) ? fd.TabHoleSides : null,
            discCutShape = fd.DiscCutShape.Any(v => v > 0.5) ? fd.DiscCutShape : null,
            discCutRotDeg = NzA(fd.DiscCutRotDeg),
            slotROutMm = double.IsNaN(fd.SlotROutMm) ? null : fd.SlotROutMm,
            totalMassG = fd.TotalMassG,
            tubeMassG = fd.TubeMassG,
            flangeMassG = fd.FlangeMassG,
            residualK = fd.ResidualK,
            checks = new Dto.Checks
            {
                rampH = fd.RampH, discOverK = fd.DiscOverK, holeFluxW = fd.HoleFluxW,
                flangeDipK = fd.FlangeDipK, tubeJ = fd.TubeJ,
            },
            // ⚠ 没做过加密复算时整组是 NaN ⇒ 存成 null，不是 0。
            //   存 0 会让「没验过」看起来像「验过且是 0」。
            verified = double.IsNaN(fd.VerifiedMeshMm) ? null : new Dto.Verified
            {
                meshMm = fd.VerifiedMeshMm,
                flangeDipK = Nz(fd.VerifiedFlangeDipK),
                holeFluxW  = Nz(fd.VerifiedHoleFluxW),
                discOverK  = Nz(fd.VerifiedDiscOverK),
                note = fd.VerifiedNote.Length > 0 ? fd.VerifiedNote : null,
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(dto, Opt), new UTF8Encoding(false));
        return path;
    }
}
