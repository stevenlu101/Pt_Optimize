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
        public double[]? tabThickMm { get; set; }
        public double[]? tabInsulMm { get; set; }
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
            if (create && Directory.Exists(Path.Combine(d.FullName, ".git")))
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

        var c = d.checks ?? throw new InvalidDataException("缺 checks（五个判据记录值）—— 没有它就无法回归对账");

        var fd = new DesignSpec
        {
            Name = Need(d.name, "name"),
            Provenance = Need(d.provenance, "provenance"),
            Binding = d.binding ?? "",
            Invalid = d.invalid ?? "",
            InvalidChecks = d.invalidChecks ?? Array.Empty<string>(),
            WallMm = NeedD(d.wallMm, "wallMm"),
            TubeInsulMm = d.tubeInsulMm ?? 10.0,
            DiscRadiusMm = NeedD(d.discRadiusMm, "discRadiusMm"),
            TabLengthMm = NeedD(d.tabLengthMm, "tabLengthMm"),
            TabHalfWidthMm = NeedD(d.tabHalfWidthMm, "tabHalfWidthMm"),
            TabThickMm = NeedA(d.tabThickMm, "tabThickMm", 4),
            TabInsulMm = NeedA(d.tabInsulMm, "tabInsulMm", 4),
            RingMul = NeedA(d.ringMul, "ringMul", 4),
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
        if (d.clampTempC is { } ct) fd.ClampTempC = ct;
        if (d.tabFilletMm is { } tf) fd.TabFilletMm = tf;
        if (d.ringWidthMm is { } rw) fd.RingWidthMm = rw;
        if (d.flangeInsulMm is { } fi) fd.FlangeInsulMm = fi;
        if (d.flangeInsulated is { } fe) fd.FlangeInsulated = fe;
        if (d.clampLengthMm is { } cl) fd.ClampLengthMm = cl;
        // A3：渐变环那三个（缺省即 NaN 哨兵 = 不逐片自定，与 DesignSpec 的默认一致）
        if (d.ringW1Mm is { Length: 4 } r1) fd.RingW1Mm = NaA(r1);
        if (d.ringW2Mm is { Length: 4 } r2) fd.RingW2Mm = NaA(r2);
        if (d.ringMul2 is { Length: 4 } t2) fd.RingMul2 = NaA(t2);
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
            discRadiusMm = fd.DiscRadiusMm,
            tabLengthMm = fd.TabLengthMm,
            tabHalfWidthMm = fd.TabHalfWidthMm,
            clampTempC = fd.ClampTempC,
            setpointC = fd.SetpointC,
            tabFilletMm = fd.TabFilletMm,
            ringWidthMm = fd.RingWidthMm,
            flangeInsulMm = fd.FlangeInsulMm,
            flangeInsulated = fd.FlangeInsulated,
            clampLengthMm = fd.ClampLengthMm,
            tabThickMm = fd.TabThickMm,
            tabInsulMm = fd.TabInsulMm,
            ringMul = fd.RingMul,
            ringW1Mm = NzA(fd.RingW1Mm),
            ringW2Mm = NzA(fd.RingW2Mm),
            ringMul2 = NzA(fd.RingMul2),
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
