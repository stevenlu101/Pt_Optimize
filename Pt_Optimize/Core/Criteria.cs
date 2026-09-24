using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PtOptimize.Core;

/// <summary>
/// **判据代号对照表**（2026-08-29，用户：「①②′②″ 这种代号要有对照表，
/// APP 内任何一处讯息与 UI 说明书内**不可以用代号说明**」）。
///
/// ══ 病在哪
///
/// 代号是**给写的人省事的**，不是给读的人用的。实测扫描全仓：
/// **197 行在判据语境里用了代号，其中 155 行没带自己的名字**。
/// 最大一类是**表头**（`②′W`、`②″K`、`③K`）—— 那里确实放不下全名，
/// 但那正是最需要图例的地方：一张全是代号的表，读的人得去别处查才看得懂，
/// 而「得去别处查」在现场就等于「不查，猜」。
///
/// ══ 单一来源
///
/// ★ 本表**不另写一份名字**。判据自己的常量已经是「代号 + 名字」
/// （<c>LineResult.Key.FlangeDip == "③ 法兰增量温降"</c>），本表从它**拆**出来。
/// 判据改名 ⇒ 对照表跟着改，不可能脱节。
/// 这也是 2026-08-29 先给 ④ 等几条补上 <c>Key</c> 常量的原因 ——
/// 没有常量就只能抄一份名字，而「同一个名字两处来源」是本仓库最常见的病。
///
/// ══ **限值不在这里**
///
/// ⚠ 本表只给「代号 = 名字（单位，方向）」，**不给限值数字**。
/// 限值只有一个来源：<c>LineCase</c>（判据自己带着 <c>Limit</c>）。
/// 在这里再写一份 10 K / 5 K，就会出现「印出来的 ≠ 判的」——
/// 本仓库为这件事栽过不止一次（见 `SingleSourceLimitTests`）。
/// 要看限值就看判据表那一列。
/// </summary>
public static class Criteria
{
    /// <summary>一条判据在对照表里的样子。</summary>
    public sealed class Entry
    {
        /// <summary>代号，如 <c>③</c>、<c>②′</c>；参考量为 <c>·</c> 开头或空。</summary>
        public string Code = "";
        /// <summary>去掉代号之后的名字，如「法兰增量温降」。</summary>
        public string Name = "";
        /// <summary>单位。</summary>
        public string Unit = "";
        /// <summary>方向：<c>≤</c> / <c>≥</c> / <c>&gt;</c>。</summary>
        public string Dir = "";
        /// <summary>一句话：它到底在管什么（给读的人，不是给写的人）。</summary>
        public string Means = "";
        /// <summary>true = 硬安全线（卡交付）；false = 参考量。</summary>
        public bool Hard;
        /// <summary>判据的完整名字（= <c>LineResult.Key</c> 里那个常量）。</summary>
        public string Key = "";
        /// <summary>
        /// ★ K 路（2026-09-15，Opus 5）：带玻璃稳态卡交付、**空管到温稳态只作参考**的判据（用户 2026-09-15：空管态只卡电流密度（管 J 与法兰截面 J）与场的有效性；2026-09-16 Opus 5 改措辞）。
        /// 不另写一份 —— 查整线判据的分工况表 <see cref="LineResult.StateKindOf"/>（与 LineRunner.Judge 同一份）。
        /// </summary>
        public bool ReferenceWhenEmptyTube => Hard && LineResult.StateKindOf(Key, emptyTube: true) is CheckKind.Reference;
    }

    /// <summary>
    /// **判据代号用到的圈号**（全程序只有这一份；拆代号、剥代号、查代号、门的正则都读它）。
    /// ★ 2026-09-14 Opus 5（复审）：加 ⑦⑧ —— 热偶读数基准的热侧／冷侧用**新代号**，不占用 ②″／③（那两个代号永远指旧判法那两条）。
    ///   此前这串字在本档里抄了四遍、在 NoCriterionCodeInUiTests 里又抄一遍；加一个代号就得改五处，漏一处门就漏看。
    /// </summary>
    public const string CodeChars = "①②③④⑤⑥⑦⑧";

    private static Entry E(string key, string unit, string dir, string means, bool hard)
    {
        // 从 Key 里**拆**出代号与名字，不另写 —— 判据改名这里自动跟着改。
        string k = key.Trim();
        string code, name;
        if (k.StartsWith("·", StringComparison.Ordinal))
        {
            name = k[1..].Trim();
            // 「· ② 法兰最高温」这种：代号是 ·②
            // R48 B（2026-09-14 Opus 5）：撇号要跟着圈号一起进代号（·②″ 这种写法），否则 ″ 会被当成名字的第一个字 —— 与下面不带「·」那一支同一个拆法。
            if (name.Length > 0 && CodeChars.Contains(name[0]))
            {
                int n = 1;
                if (name.Length > 1 && (name[1] == '′' || name[1] == '″')) n = 2;
                code = "·" + name[..n];
                name = name[n..].Trim();
            }
            else
            {
                // ★ 没有圈号的参考量**本来就没有代号**（2026-08-29 修）。
                //   此前它们全被塞成同一个「·」—— 那不是代号，是**分类标记**，
                //   于是九条参考量共用一个「代号」，Of("·") 说不清指哪条。
                //   ⇒ 空代号 = 明确表示「这条没有代号，按名字认」。
                code = "";
            }
        }
        else if (k.Length > 0 && CodeChars.Contains(k[0]))
        {
            int n = 1;
            if (k.Length > 1 && (k[1] == '′' || k[1] == '″')) n = 2;
            code = k[..n];
            name = k[n..].Trim();
        }
        else { code = ""; name = k; }
        return new Entry { Code = code, Name = name, Unit = unit, Dir = dir,
                           Means = means, Hard = hard, Key = key };
    }

    /// <summary>
    /// 全表。顺序 = 硬安全线在前、参考量在后，各自按代号。
    /// ⚠ 「意思」那一列写的是**失效模式**，不是公式 —— 读的人要判的是「离哪个坏结果近」。
    /// </summary>
    public static readonly Entry[] All =
    {
        // ── 硬安全线（卡交付）
        E(LineResult.Key.Ramp,      "A/mm²", "≤", "按设定速率（20 °C/h）空管升温所需电流折成的管 J 峰值；没被管 J 许用截住 = 升得到目标（闭式，R20）", true),
        E(LineResult.Key.RampHours, "h",     "≤", "参考：集总模型（含法兰质量与自热）算的升温到位用时", false),
        // ★★★★★ 决 103（2026-09-24）：带玻璃稳态卡交付的热侧、冷侧换成下面两条；「管孔净流入」「最热铂高出热偶读数」「管根低于热偶读数」降为参考（最后一个参数 true → false，行挪进参考量那组的位置不变、分组按 Hard 取）。
        //   本表描述生产口径（决103）；改回口径只供门，界面不出现。
        E(LineResult.Key.HotOverContact, "K", "≤", "每片法兰温度场的最高温比该片管接触处温度（模型算的管根接触温度）高多少。"
            + "带玻璃稳态电流小，法兰要靠自身焦耳热撑到接触处温度以上一点才不当散热片；比接触处略热的方向是对的，但最多高出允许值（参数表「法兰最热处高出管接触处温度 允许值」，默认 10）", true),
        E(LineResult.Key.TubeToFlangeHeat, "W", "≤", "管孔处由管流入法兰的净热流：为正就是法兰在抽管子的热、把管在接触处的温度拉低 —— 这个方向不许，限值 0、不给预算。"
            + "与「管孔净流入」是同一个量，合格方向相反", true),
        E(LineResult.Key.NetFlux,   "W",     ">", "（只作参考，不卡交付）热是从管子流进法兰，还是从法兰流进管子；带玻璃稳态卡交付的是反方向的「管接触处流入法兰的净热流 ≤ 0」", false),
        // ★★★★★ R48 B（2026-09-14 Opus 5）：热侧换成热偶读数基准（旧的「圆盘区最高温 − 管温」挪到下面参考量那一组，说明文字与修订史原样留着作对照）。
        //   写意图，不写限值：限值只从 LineCase 读。
        //   ★ U 路（2026-09-18，Opus 5）：限值改成工程师填的（参数表「最热铂高出热偶读数 允许值」，默认 = 热偶在 1100 °C 的误差）
        //     ⇒ 这一列**连默认值都不再印**：本表是全表说明，会被不同预算的算例共用，印一个数就会出现「印出来的 ≠ 判的」。要看限值就看判据表那一列。
        E(LineResult.Key.HotOverTc, "K",     "≤", "法兰上（圆盘和舌片）以及它贴着的管根里，最热的那一点比控温热偶的读数高多少。"
            + "控温热偶装在每段中点，读数上下一个误差带以内分不出来，"
            + "所以任何一处铂比读数高出这个带，就可能已经越过设定而热偶看不出来。"
            + "允许高出多少由参数表的「最热铂高出热偶读数 允许值」定（默认 = 热偶在 1100 °C 的误差），判据表那一列印的就是它。"
            + "端片的基准是本段热偶的读数；两段共用的那一片，基准取两侧热偶读数的对数平均（按开尔文算）。"
            + $"判据说明里并列「模型算的无法兰交界管温」作对照，它与基准差超过 {ThermocoupleBasis.ModelGapNoteK:0} ℃ 时会写明 —— 判定只按热偶读数，"   // 阈值只从 ThermocoupleBasis 读（2026-09-14 Opus 5 复审：原写死「1 ℃」）
            + "因为模型交界温度会随设计变量挪动。只作参考，不卡交付（带玻璃稳态卡交付的热侧是「法兰最热处高出管接触处温度」）", false),
        E(LineResult.Key.FreeTab,   "mm",    "≥", "舌片伸出来、没被压接吃掉的那一段够不够长 —— 现场铜排装得下吗", true),
        E(LineResult.Key.DiscCover, "mm",    "≥", "圆盘半径够不够盖住管孔加焊脚 —— 盖不住就焊不出来", true),
        // ⚠ 「意思」这一列进界面全表 ⇒ **不带人称**（说明书里不许出现「用户」；出处写在 HANDOVER §1.83 那张表与 WrapLimits.SourceNote）
        // ★★★ 2026-09-18（Opus 5）：**由卡交付改为参考量**（最后一个参数 true → false）——
        //   APP 只出材质与各区厚度方案，包法（缠绕还是预制保温块）由现场定，见 WrapLimits.PlanOnlyNote。
        //   行的位置没动（分组按 Hard 这个标志取，不按位置），只改标志与「意思」这一列。
        E(LineResult.Key.WrapTurns, "mm",    "≤", "管–盘接合区（圆盘靠管孔的内环带）的圆盘保温厚度折成几圈；现场一圈 0.5 mm，一次缠绕缠到 20 圈以上会渐成球形，超过就改用预制保温块。只作参考、不卡交付：交付的是材质与各区厚度方案，怎么包由现场定。管保温与舌板保温好缠绕，不在此列", false),
        E(LineResult.Key.TubeJ,     "A/mm²", "≤", "管子自身的电流密度上限（取参数表「管许用电流密度」与「管 J 使用上限」的较小值）", true),
        E(LineResult.Key.TubeJPre103, "A/mm²", "≤", "对照：管 J 对原许用值（参数表「管许用电流密度」）的比较，只作参考", false),
        E(LineResult.Key.SectionJ,  "A/mm²", "<", "法兰每一个必经截面（舌片各处含开孔、舌盘交界、孔缘环与各级环）的电流密度 = 升温设计电流 ÷ 截面积；按 10 定尺寸，全体要小于 11", true),

        // ★★★★★ R48 B（2026-09-14 Opus 5）：冷侧取代旧判法「法兰增量温降（靶）」，升为硬安全线；原先「靶」这一组就空了，组标题一并删掉。
        E(LineResult.Key.ColdUnderTc, "K",   "≤", "法兰所在接头处管根较冷的那一端，比控温热偶的读数低多少 —— 法兰抽热会把管根拉冷。"
            + "控温热偶装在每段中点，管根低出误差带，热偶就看不出这一段已经偏冷。"
            + "允许低出多少由参数表的「管根低于热偶读数 允许值」定（默认 = 热偶在 1100 °C 的误差），判据表那一列印的就是它。"
            + "基准与上一条相同：端片取本段读数，共用的那一片取两侧读数的对数平均（按开尔文算）；"
            + $"说明里同样并列「模型算的无法兰交界管温」作对照，差超过 {ThermocoupleBasis.ModelGapNoteK:0} ℃ 时写明。只作参考，不卡交付（带玻璃稳态卡交付的冷侧是「管接触处流入法兰的净热流」）", false),

        // ── 参考量（印出来，不卡交付）
        // R48 B（2026-09-14 Opus 5）：下面这条原是硬安全线（圆盘区最高温 − 管温），降为参考量（旧判法，代号 ②″ 不变）；说明文字与修订史原样留着。
        // ★ R48（2026-09-14，Opus 5）：这句以前写「贴着管孔那一圈盘面」「孔周电流拥塞顶出来的尖峰」，
        //   两处都与代码/实测对不上：
        //   ① 代码圈的是 sqrt(x²+z²) ≤ 盘半径 —— **整块圆盘**，不是贴着孔那一圈（见 ShellThermal 的分区）；
        //   ② 「孔周电流拥塞」实测不成立 —— 峰位 r = 29.0～29.2 mm，而孔半径 25.8、孔边定温带只到 28.8，
        //      峰在盘外缘内侧约 0.8 mm，不在孔边。机理未坐实 ⇒ **删掉那半句，不换另一个猜测**。
        //   界面上曾因此出现两个互相矛盾的圆盘区定义（说明书页印这句，判据表印新口径的值）。
        // ★ R48 续（2026-09-14，Opus 5；物理把关人两条意见）：
        //   ③ 「舌片离管子几十毫米」在两个内置档上**不成立**（舌区峰 r = 31～33，离孔 5～7 mm）。
        //      这句原本要说的是**中间隔着什么**，不是距离 ⇒ 改成拓扑说法，任何形状都成立。
        //   ④ 「它的温度**由**熔点和局部热失稳两条**管**」—— 局部热稳定现在是参考项，**不卡交付**。
        //      说明书上写着有人管、实际没人卡，正是「不许把事情搞混」。
        //      改成「另见」：只指路，不承诺它会挡住设计。升不升硬判据，等重解后在复核网格上看过裕度再定。
        E(LineResult.Key.DiscTemp,  "K",     "≤", "（旧判法，只作对照，不卡交付）圆盘上最热的那一点，比贴着的管子高多少 —— 基准是模型算的管根温度，不是热偶读数。圈的是圆盘整块 —— "
            + "从管孔边缘一直到圆盘外缘，不含伸出去的舌片。"
            + "圈这块，是因为它经焊缝直接贴着管子：这块只要比管子热，热就直接进管；"
            // 2026-09-14 再改（物理把关人）：「路上有散热」在圆盘包厚保温时不成立（实测四片圆盘散热只有 12～14 W，而法兰发热 440～800 W）。
            //   换成与保温取值无关的说法：舌片的热要进管子，必须先把圆盘顶热，所以看圆盘就够了。
            + "舌片上的热要进管子，必须先经过圆盘、先把圆盘顶热，所以看圆盘就够了，舌片不在这条里算。"
            + "舌片自己的温度另见熔点与局部热失稳两项", false),
        // R48 B（2026-09-14 Opus 5）：原是靶（法兰增量温降 ≤ 10），降为参考量（旧判法，代号 ③ 不变）。
        E(LineResult.Key.FlangeDip, "K",     "≤", "（旧判法，只作对照，不卡交付）**法兰把管根拉冷了多少** = 无法兰基线 − 实际 —— 只算法兰的责任，不含控温点梯度", false),
        E(LineResult.Key.TubeStrength,  "—",     "≤", "管子的强度用掉了几成（Pt 持久强度实测区间外时**判不了**）", false),
        E(LineResult.Key.FlangeTopTemp, "K",     "≤", "整片法兰（含舌片）最高温比管温高多少", false),
        E(LineResult.Key.SetpointDrift, "K",     "≤", "管温偏离本段控温点多少 —— **由控温点梯度决定，法兰管不着**", false),
        E(LineResult.Key.SelfSupply,    "—",     "≥", "法兰自身发热够不够养活自身散热", false),
        E(LineResult.Key.FlangeJ,       "A/mm²", "≤", "法兰上的电流密度峰值（≠「局部热稳定」那条）", false),
        E(LineResult.Key.FlangeStab,    "×",     "≥", "整片热稳定：散热随温度涨得比发热快多少倍；小于 1 即整片会自行升温直到烧断（带玻璃稳态卡交付）", true),
        E(LineResult.Key.LocalStab,     "×",     "≥", "局部热稳定：全片逐格算，最不稳定那一格离热失控还有几倍余量；小于 1 即该处会自行升温直到烧断（带玻璃稳态卡交付）", true),
        E(LineResult.Key.RampField,     "K",     "≤", "现场升温过程中「法兰−管」温差的全程最大值", false),
        E(LineResult.Key.HeatBalance,   "W",     "≈", "管子失去的热与法兰收到的热对不对得上（守恒对账）", false),
        E(LineResult.Key.HeatResidual,  "W",     "≈", "单片法兰自己的热平衡残差，应接近 0", false),
        E(LineResult.Key.GlassDrop,     "K",     "≤", "玻璃温降与现场实测差多少", false),
    };

    /// <summary>
    /// 按代号找。找不到返回 null（**不要**编一个出来）。
    ///
    /// ⚠ 空串/空白**不是代号**，一律返回 null（2026-08-29 修）：
    ///   没有圈号的参考量 <see cref="Entry.Code"/> 是空的，
    ///   若不挡住，<c>Of("")</c> 会命中其中随便一条 —— 那是个说不清指哪条的答案，
    ///   而它**看起来完全正常**。
    /// </summary>
    /// <summary>
    /// ★★★ **界面用的判据名：把代号剥掉**（2026-08-30，用户原话「工程师看不懂」）。
    ///
    /// ══ 为什么代号会流到界面上
    ///
    /// 判据的 <see cref="LineResult.Key"/> 常数**自己就带着代号**：
    /// <code>
    ///   NetFlux     = "②′管孔净流入"
    ///   HotOverTc   = "⑦ 最热铂高出热偶读数"          （R48 B 2026-09-14 起的新判据，新代号）
    ///   FlangeDip   = "③ 法兰增量温降（旧判法）"      （代号 ③ 不换主人，只降为参考量）
    /// </code>
    /// 而判据表直接印 <c>ConstraintOut.Name</c> ⇒ 代号是**从内核流到界面上**的，
    /// 散落在各处提示语里的那些只是支流。
    ///
    /// ══ 为什么不改 Key
    ///
    /// Key 是全仓**唯一来源**：门（<c>Flow.RequiredChecks</c>）、判据匹配
    /// （<c>Name.StartsWith(key)</c>）、命令行、测试、交接文档都靠它。
    /// 改它等于同时改识别与显示两件事 —— 而只有显示要改。
    /// ⇒ **内部身份不动，显示层剥壳**，剥壳只有这一个函数。
    ///
    /// ══ 为什么代号必须走干净，而不是「带上解释就行」
    ///
    /// 2026-08-29 做过一版「代号必须带解释」（对照表 + 展开 12 处）。**不够**：
    /// 判据代号 ⑤（舌片自由段）与页签上的**阶段号** ⑤（交付）**形状相同、含义无关**，
    /// 摆在同一个界面上必然误读。⇒ 界面侧根本不出现代号。
    /// 对照表留着给**命令行与文档**用 —— 那两处的读者是开发者。
    /// </summary>
    // ════════════════════════════════════════════════════════════════════
    //  ★★★★★ 判词与整行 —— **全仓唯一一份写法**（2026-09-18，Opus 5）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 一条判据的**判词**（「过 / 不过 / 无法判定 / 参考（…）」）—— 只有这一份写法。
    ///
    /// ★★ 病的形状（2026-09-18 旁证 A 第 2.4(c) 节 实测）：参考量写死 <c>Ok = true</c>，
    /// 于是输出里出现「**过**，裕度 −0.401」同行并列 —— 判词说过、裕度说越限。
    /// 那正是「不许把事情搞混」要挡的形态。
    /// ⇒ <b>参考量不印「过 / 不过」</b>，只印「参考（…）」；数与裕度照印，读的人自己看它越没越 1。
    ///
    /// ⚠ 「本工况只作参考」是另一回事：那由 <c>Kind</c> 按工况取（<c>LineResult.StateKindOf</c>），
    ///   而**不是**这里的 Reference —— 分工况降级的那几条在本工况里 Ok 是真算出来的，照印判词。
    ///
    /// ⚠ 原来这段逻辑在 <c>InstallReport</c> 里写了一份、八个长跑门各手抄了一份
    ///   （手抄那份还把参考量印成「过」）。提到这里，谁都不许再抄。
    /// </summary>
    public static string Verdict(ConstraintOut c)
    {
        if (c is null) throw new System.ArgumentNullException(nameof(c));
        return c.Kind == CheckKind.Reference
             ? (c.Withheld ? "参考（暂不给数）" : c.Undetermined ? "参考（算不出）" : "参考（不卡交付）")
             : c.Undetermined ? "无法判定" : c.Ok ? "过" : "不过";
    }

    /// <summary>这一条**卡不卡交付**的一句话（判据表尾的方括号）。</summary>
    public static string KindTag(ConstraintOut c)
    {
        if (c is null) throw new System.ArgumentNullException(nameof(c));
        return c.Kind switch
        {
            CheckKind.HardSafety => "卡交付（硬安全线）",
            CheckKind.Target => "卡交付（目标）",
            _ => "只作参考",
        };
    }

    /// <summary>
    /// 一条判据的**整行**（命令行判据表、长跑报告共用）：
    /// 「名：实际 x / 限值 y 单位，判词，裕度 z，位置 w　[卡不卡交付]」。
    /// 判据名走 <see cref="Plain"/>（界面与报告不许出现判据代号）；判词走 <see cref="Verdict"/>。
    /// </summary>
    public static string OneLine(ConstraintOut c)
    {
        if (c is null) throw new System.ArgumentNullException(nameof(c));
        double slack = double.IsNaN(c.Actual) ? double.NaN
                     : c.LessIsBetter ? c.Limit - c.Actual : c.Actual - c.Limit;
        string verdict = Verdict(c);
        // 不过 / 无法判定 要跳出来（参考量不加星，它本来就不参与判定）
        if (c.Kind != CheckKind.Reference && verdict != "过") verdict = "**" + verdict + "**";
        return $"{Plain(c.Name)}：实际 {(c.Withheld ? "暂不给数" : double.IsNaN(c.Actual) ? "—" : c.Actual.ToString("0.###"))}"
             + $" / 限值 {c.Limit:0.###} {c.Unit}，{verdict}，"
             + $"裕度 {(double.IsNaN(slack) ? "—" : slack.ToString("+0.###;-0.###"))}，位置 {c.Where}　[{KindTag(c)}]";
    }

    /// <param name="nameOrKey">判据名或 Key（<c>ConstraintOut.Name</c> 直接传进来即可）。</param>
    public static string Plain(string? nameOrKey)
    {
        if (string.IsNullOrWhiteSpace(nameOrKey)) return "";
        string k = nameOrKey.Trim();

        // 「· ② 法兰最高温」这种：先剥分类标记，再剥代号
        if (k.StartsWith("·", StringComparison.Ordinal)) k = k[1..].TrimStart();
        if (k.Length > 0 && CodeChars.Contains(k[0]))
        {
            int n = 1;
            if (k.Length > 1 && (k[1] == '′' || k[1] == '″')) n = 2;
            k = k[n..].TrimStart();
        }
        return k;
    }

    /// <summary>
    /// 界面里出现代号就是违规 —— 给门用的判定（<c>true</c> = 这段文字里有代号）。
    /// ⚠ 只认**判据代号**：圈号后面跟 ′ ″ 的，或圈号紧贴汉字的。
    ///   页签上的阶段号带全名（「① 先决条件（能造·能升温）」）是导航编号，不在此列 ——
    ///   但那种写法里圈号后面是空格加中文，与判据代号形状一样，
    ///   所以门只扫**判据相关**的字符串，不做全局正则。见 UiNoCodeTests。
    /// </summary>
    public static bool HasCode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = 0; i < text.Length; i++)
            if (CodeChars.Contains(text[i])) return true;
        return false;
    }

    /// <summary>
    /// 判据的单位，按**名字**查（<c>ConstraintOut.Name</c> 直接传进来即可）。
    ///
    /// 用户 2026-09-02 那句「代号全换成全名（<b>+单位</b>）」的后半截：
    /// 判据表原来只印「429.580 / 限 10.000」，**没有一个字说这是 K 还是 W** ——
    /// 而 ③ 是 K、②′ 是 W、⑤⑥ 是 mm、管 J 是 A/mm²，四种单位混在同一张表里。
    ///
    /// ⚠ 先按全名精确匹配，匹配不上再按前缀 —— <c>Name</c> 往往比 <c>Key</c> 长
    ///   （如「③ 法兰增量温降 ≤ 上限」）。<b>找不到就返回空字符串，绝不编一个单位。</b>
    /// ⚠ 单位是「—」的（无量纲比值）不算单位，也返回空 —— 印出来只是噪音。
    /// </summary>
    public static string UnitOf(string? nameOrKey)
    {
        string p = Plain(nameOrKey);
        if (p.Length == 0) return "";
        var hit = All.FirstOrDefault(e => Plain(e.Key) == p)
               ?? All.FirstOrDefault(e => p.StartsWith(Plain(e.Key), StringComparison.Ordinal));
        string u = hit?.Unit ?? "";
        return u == "—" ? "" : u;
    }

    public static Entry? Of(string code) =>
        string.IsNullOrWhiteSpace(code) ? null : All.FirstOrDefault(e => e.Code == code);

    /// <summary>
    /// 单个代号的展开，用在**散文**里：<c>③（法兰增量温降）</c>。
    /// 找不到就原样返回代号 —— 不许编名字。
    /// </summary>
    /// ★★ 2026-08-30 起**只给名字，不给代号**（用户：「工程师看不懂」）。
    ///   此前返回「⑤（舌片自由段）」—— 那是 08-29 那版「代号必须带解释」的做法。
    ///   改这一处，所有调用点跟着对。
    ///   ⚠ 代号本身仍在 Entry.Code 上，命令行与文档照常用。
    ///   ⚠⚠ 判据的 Key 常数（LineResult.Key.NetFlux = "②′管孔净流入"）**绝不能动** ——
    ///     它是识别用的唯一来源。2026-08-30 我用正则全仓替换时把它也改了，
    ///     变成「管孔净流入管孔净流入」，四条门当场红。剥壳只准在显示层做。
    public static string Explain(string code)
    {
        var e = Of(code);
        return e is null ? code : e.Name;
    }

    /// <summary>
    /// **表格图例**：用在拿代号当列头的表底下。
    /// 只给「代号 = 名字（单位 方向）」，**不给限值数字**（限值只从判据读）。
    /// </summary>
    public static string Legend(params string[] codes)
    {
        var parts = new List<string>();
        foreach (var c in codes)
        {
            var e = Of(c);
            if (e is null) continue;
            parts.Add($"{e.Code} = {e.Name}（{e.Unit}，{e.Dir}）");
        }
        return parts.Count == 0 ? "" : "　代号：" + string.Join("｜", parts) + "　限值见判据表";
    }

    /// <summary>
    /// 说明书用的 HTML 表。**与 <see cref="Table"/> 同一份数据** ——
    /// 文本一份、HTML 一份地各写各的，正是「同一件事两处来源」。
    /// </summary>
    public static string Html()
    {
        var sb = new StringBuilder();
        sb.Append("<h3>判据全表</h3>");
        sb.Append("<p>这是 APP 会判、会印出来的<b>全部</b>判据。名字与单位跟屏幕上一模一样 —— ");
        sb.Append("判据表里那行字长什么样，这里就长什么样，不用换算、不用对照。</p>");
        sb.Append("<p>⚠ <b>本表不含限值数字</b> —— 限值只有一个来源（判据自己）。");
        sb.Append("要看限值请看下面「限值的出处」那张表，两张表不会打架。</p>");
        foreach (var hard in new[] { true, false })
        {
            sb.Append(hard ? "<h4>硬安全线（不过就不能交付）</h4>"
                           : "<h4>参考量 / 靶（印出来，不卡交付）</h4>");
            // ★★★ 2026-09-03：名字列改印 **Plain(Key)**，不再印 Key 本身。
            //   Key 自带代号（"②′管孔净流入"），而**判据表画到屏幕上时走的就是 Plain**
            //   （LineDesignPage 三处都是 Criteria.Plain(c.Name)）⇒ 印 Key 反而与屏幕对不上。
            //   ⚠ 连带改了 UiWiring 那条「说明书里查得到」的门：它原来找 Key 原文，
            //     现在找 Plain(Key) —— 找的是**工程师真的看得见的那个字串**，比原来更准。
            //   ⚠⚠ Key 常量本身一个字都没动（它是识别用的唯一来源）。
            // K 路（2026-09-15，Opus 5）：硬安全线那张表多一列「空管到温稳态」—— 取自整线判据的分工况表（Entry.ReferenceWhenEmptyTube），不另写
            sb.Append("<table class=\"nw\"><tr><th>判据（APP 里显示的名字）</th><th>单位</th>"
                    + "<th>方向</th>" + (hard ? "<th style=\"white-space:nowrap\">空管到温稳态</th>" : "") + "<th>它在管什么</th></tr>");
            foreach (var e in All.Where(x => x.Hard == hard))
                sb.Append($"<tr><td><b>{Plain(e.Key)}</b></td><td class=\"n\">{e.Unit}</td>"
                        + $"<td class=\"n\">{e.Dir}</td>"
                        + (hard ? $"<td class=\"n\" style=\"white-space:nowrap\">{(e.ReferenceWhenEmptyTube ? "只作参考" : "卡交付")}</td>" : "")   // 抓图（2026-09-15 Opus 5）：不加 nowrap 时这一列被挤成一字一行
                        + $"<td>{BoldHtml(e.Means)}</td></tr>");   // R47 第三轮 N6：** 转粗体，不许字面印进说明书
            sb.Append("</table>");
        }
        // ★ K 路（2026-09-15，Opus 5）：分工况判据的说明（说明书是给工程师看的使用说明 ⇒ 不带人称，不写「用户」）。只作参考的判据名单从分工况表取。
        var emptyRef = All.Where(x => x.ReferenceWhenEmptyTube).Select(x => Plain(x.Key)).ToArray();
        sb.Append("<p>★ <b>两种稳态工况，卡交付的判据不一样</b>：带玻璃稳态（管内有热玻璃，电流小，法兰主要当散热片）「硬安全线」那张表每一条都卡交付；");
        var emptyHard = LineResult.RequiredFor(emptyTube: true).Select(q => Plain(q.Prefix)).ToArray();
        sb.Append($"空管到温稳态（设备升到温、进玻璃之前，电流大）卡交付的只有「{string.Join("」「", emptyHard)}」，");
        sb.Append(emptyRef.Length == 0 ? "" : $"「{string.Join("」「", emptyRef)}」在空管态照常计算、照常印在判据表里，只作参考，不卡交付。");
        // 2026-09-16 Opus 5（K 路复审修）：有效性清单补「图纸推不出保温分界」（与 LineResult.FieldUndeterminedReasons 读的位一一对应）；再补三个工况的先后（用户 2026-09-15 定的地位）。
        sb.Append("两种工况都一样的是<b>场要解得出来</b>：场没收敛、越过铂熔点、超出散热表的温度范围、压接段盖到管孔或伸进圆盘、图纸推不出保温分界时，该工况判不了 —— 判不了不算过。");
        sb.Append("先后次序：先过升温全程（本表里的「升温」一条两态都卡；升温期的其他判据另行落地），带玻璃稳态决定法兰设计成不成，空管到温稳态只卡电流密度与场要解得出来。</p>");
        // R48 B（2026-09-14 Opus 5）：热侧换成热偶读数基准；再补一段说基准怎么取、为什么不用模型温度。
        // ★ 2026-09-14 Opus 5（复审）：原来写「管孔净流入与最热铂高出热偶读数是同一条安全线的两个视角、缺一条就漏一个方向」——
        //   对旧的「圆盘区最高温 − 管温」大致成立（两者都在问热会不会从法兰进管），对新热侧**不成立**：新热侧还算舌片区峰和管根较热端，
        //   基准是设定值 ⇒ 净流入 > 0（热往法兰走）时它照样可以超限（例如共用接头的管根本身就比对数平均高），反过来也一样。
        //   说明书在教工程师一个不对的推论 ⇒ 改成「各管各的」。
        // 决 103（2026-09-24）：带玻璃稳态的判据换向 —— 说明书先讲现行的两条，再保留旧两条的说明（它们照印作参考）。不带人称。
        sb.Append("<p>★ <b>带玻璃稳态卡交付的热侧与冷侧</b>（2026-09-24 起）：每一片法兰都比它贴着的那段管子的接触处温度来判 —— ");
        sb.Append("「法兰最热处高出管接触处温度」不超过允许值（默认 10 K），「管接触处流入法兰的净热流」不大于 0。");
        sb.Append("电流小时法兰天然是散热鳍片，会把管在接触处的温度拉低，这个方向不许；法兰靠自身发热撑到接触处温度以上一点是对的，最多高出允许值。");
        sb.Append("接触处温度是模型算的该片管根温度：端片取相邻那一段的端温，两段共用的那一片取两侧段端温度的较高者；控温热偶的设定值只在说明里印作对照。");
        sb.Append("局部热稳定（全片逐格算）与整片热稳定在带玻璃稳态也卡交付（须不小于 1）。");
        sb.Append("下面两段讲的「管孔净流入」「最热铂高出热偶读数」「管根低于热偶读数」照常计算、照印在参考量里，不卡交付。</p>");
        sb.Append("<p>★ <b>「管孔净流入」与「最热铂高出热偶读数」管的是两件不同的事，要分开看</b> —— ");
        sb.Append("前者管<b>热流方向</b>：热该从管子流进法兰，不该倒灌进管子（烧断的方向）；");
        sb.Append("后者管<b>铂比控温热偶的读数高出多少</b>：法兰上（圆盘、舌片）以及它贴着的管根，任何一处比读数高出误差带就说不清了。");
        sb.Append("两者不能互相推出：热往法兰走时铂照样可以比读数高出误差带（比如两段共用的那一片，一侧管子本身就比两侧读数的对数平均热），");
        sb.Append("反过来铂没有高出读数时热也可能在往管里灌。所以两条各自都要过。</p>");
        sb.Append("<p>★ <b>「最热铂高出热偶读数」与「管根低于热偶读数」的基准都是控温热偶的读数</b>：热偶装在每段中点，");
        // ★ U 路（2026-09-18，Opus 5）：两条限值是**参数表里的输入**（默认 = 热偶在 1100 °C 的误差），不在这里印数 —— 印了就会与判据表那一列漂开。
        sb.Append("读数上下各算一次，所以热侧、冷侧各自一条；每一侧允许差多少由参数表的「管根低于热偶读数 允许值」「最热铂高出热偶读数 允许值」定，默认都是热偶在 1100 °C 的误差。");
        sb.Append("端片取本段读数；两段共用的那一片取两侧读数的<b>对数平均</b>（按开尔文算）。");
        sb.Append($"基准只用设定值算，不用模型算的交界管温 —— 那个温度会随设计变量挪动；它只在判据说明里并列作对照，与基准差超过 {ThermocoupleBasis.ModelGapNoteK:0} ℃ 时写明。");
        sb.Append("「圆盘区最高温 − 管温」「法兰增量温降」是换基准之前的旧判法，现在只列在参考量里作对照，不卡交付。</p>");
        return sb.ToString();
    }

    /// <summary>
    /// R47 第三轮 N6（2026-09-13）：「它在管什么」那一列的文案用 <c>**</c> 标粗（与判据 Note、页顶横幅同一套写法），
    /// 进 HTML 要转成 &lt;b&gt;，否则说明书上就是两对字面星号（DocRefTests 钉着）。奇数个 ** 时最后一段照原样。
    /// </summary>
    public static string BoldHtml(string s)
    {
        string[] parts = System.Net.WebUtility.HtmlEncode(s).Split("**");
        var b = new StringBuilder();
        for (int i = 0; i < parts.Length; i++) b.Append(i % 2 == 1 ? "<b>" + parts[i] + "</b>" : parts[i]);
        return b.ToString();
    }

    /// <summary>完整对照表，给 `--glossary` 与说明书用。</summary>
    public static string Table()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 判据代号对照表 ===");
        sb.AppendLine("⚠ 本表**不含限值数字** —— 限值只有一个来源：判据自己（LineCase）。");
        sb.AppendLine("  在这里再抄一份，就会出现「印出来的 ≠ 判的」。要看限值就看判据表那一列。");
        sb.AppendLine();
        foreach (var group in new[] { true, false })
        {
            sb.AppendLine(group ? "── 硬安全线（不过就不能交付）" : "── 参考量 / 靶（印出来，不卡交付）");
            foreach (var e in All.Where(x => x.Hard == group))
                sb.AppendLine($"  {(e.Code.Length == 0 ? "·" : e.Code),-3} {e.Key,-22} {e.Unit,-6} {e.Dir,-2}  {(e.ReferenceWhenEmptyTube ? "（空管到温稳态只作参考）" : "")}{e.Means}");   // K 路（2026-09-15 Opus 5）
            sb.AppendLine();
        }
        sb.AppendLine("★ 代号里的 ′ 与 ″ 不是次要标记：**②′ 与 ②″ 是同一条安全线的两个视角** ——");
        sb.AppendLine("  ②′ 从管子看热流方向，②″ 从法兰看圆盘区温度（贴着的管根作基准）。");
        // R48 B（2026-09-14 Opus 5 复审）：代号**不换主人** —— ②″／③ 仍指原来那两条（2026-09-14 起降为参考量），新判据用新代号。
        sb.AppendLine("★ 2026-09-14 起卡交付的热侧／冷侧换成热偶读数基准：⑦ 最热铂高出热偶读数、⑧ 管根低于热偶读数（新代号）；");
        sb.AppendLine("  ②″／③ 仍指圆盘区最高温 − 管温／法兰增量温降（旧判法），降为参考量，历史记录、注释、HANDOVER 里的 ②″／③ 含义不变。");
        sb.AppendLine("  ⚠ ②′ 与 ⑦ **不是**同一条线的两个视角：⑦ 的基准是设定值，还算舌片区峰与管根较热端，两条各自都要过。");
        sb.AppendLine("★ 2026-09-24 起（决 103）带玻璃稳态卡交付的热侧／冷侧换成「法兰最热处高出管接触处温度」「管接触处流入法兰的净热流」（不带代号）；");
        sb.AppendLine("  ②′／⑦／⑧ 三条降为参考量（代号含义不变），局部与整片热稳定升为硬线，管 J 限值与使用上限 11 取小。");
        return sb.ToString();
    }
}
