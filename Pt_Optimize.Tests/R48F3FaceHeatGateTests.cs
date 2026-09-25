using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  F3 发热改按面（2026-09-23，执行计划组 C 的 C2；HANDOVER 决 27 = 面发热作交付口径，决 26 = 命令行仪器不切）—— 门槛**跑前写死**，跑完不挪。
//
//  改了什么（算式在 ShellCurrent 的 FaceGenW／HeatJAPerMm2 注释里）：每格发热 q_i = ½ Σ_面 Q·ΔV（压接面、孔面那一份整份给自由格），
//  热场改吃「发热等效 J」= √(q_i ÷ (ρ(T_解)·t·A))；JMax、界面 J、移除优先级仍是重构 J。改回参数 faceHeat: false（整线：LineCase.GateRevertFaceHeat），生产不传。
//
//  ══ 门
//   门1 闭合（快）：R48NMeshGateTests「带格与闭合」那 18 例（W08 片0，9 个几何 × 导航／判决，等温 1214 A／1150 °C）上
//       新 P_gen/P_net ∈ 1 ± 1e-6，P_gen = Σ 面发热，P_net = I²·ρ/CurrentInA（离散网络真耗散）；热场口径 Σ ρ(T_解)·J_等效²·t·A 同样 ∈ 1 ± 1e-6。
//       阈值出处：Σ_面 g·ΔV² = CurrentInA − Σ_自由格 V_i·r_i 是恒等式（内部面两侧各算一次、电极 V = 1、孔 V = 0）。
//       （2026-09-23 审查后改写）生产 CG（Jacobi-PCG）从零初值起，x_k ∈ span{p_0..p_{k-1}}、r_k ⊥ p_j ⇒ Σ V r = x_k·r_k 在精确算术下对任何迭代步都为 0；
//       所以闭合只剩舍入，与电位解是否收敛无关（审查探针：R31 判决网格 maxIter = 5、未收敛时闭合仍为 −6.0e-15）。1e-6 是**选定值**，在 PCG 路径上对舍入有约 7 个数量级余量。
//       印出的「残差界」max|V|·Σ|r|/I_in 只是一个松界（18 例 7.8e-8～4.4e-7），1e-6 只比它大约 2～13 倍，不是「三个数量级」；闭合 1e-13 的来源是上面的正交性，不是正负相消。
//       GS 旧路径（LinearGaussSeidel = true，--gslinear，不得用于交付）不满足这一条：收敛时闭合约 8e-7，离 1e-6 不到一个数量级 —— 本门不覆盖该路径。
//       同一例上还判：发热等效 J 全是有限非负数、表达不了的发热 = 0；改回（faceHeat: false）⇒ HeatJAPerMm2 就是重构 J 数组本身，且电位、重构 J、JMax、
//       TotalGenW、面发热、归一化电流两种取值逐位相同（开关只换交给热场的那一个数组）。旧 [0.99, 1.01] 门（R48NMeshGateTests，量 TotalGenW）保留、不收紧，本门只印它。
//       覆盖：W08 片0 等温场的 18 个形状上：面循环每条导电面恰好记一次（面类别不漏记、不重记）、定标常数 scale²·ρ_ref、sqrt 往返（热场口径闭合）、HeatJ 与热场 ρ 同口径；
//       不覆盖：电位解的收敛与精度（由同一门里的 on.Converged 另判）、σ(T) 电流场（生产缺省关）、GS 线性求解路径、非纯铂牌号（ThermalFormGen 写死 PtProps.Pure）、
//       W06、片1～3、图纸路径、局部分布对不对（闭合只证总量；「每面各一半」是约定，见门2）。
//   门2 条带（快）：手造条带 x ∈ [0, 20] 步长 1，压接段 [0, 4]（整面接触、面上定电位），x = 20 孔面上定电位。
//       均匀厚度（J 均匀）：每个自由格面发热与 ρJ²tA 逐格相对差 ≤ 1e-9（理论相同：两面各半 = h/2 + h/2 = 格长）；
//       厚度锥形 t = 1 + 0.25·列号（J ∝ 1/t 沿程变）：两者逐格不同，最大相对差 ≥ 1e-3（门不空转），且逐格等于解析预测
//       t_i·(左 + 右)，左／右 = 压接面或孔面 ½/t_i，内部面 ½·(t_a + t_b)/(2 t_a t_b)（调和平均导度的串联电阻各分一半），相对差 ≤ 1e-9。
//       阈值出处：1e-9 = 条带在 CG 容差 1e-15 下的舍入余量（选定，与 R48ClampFaceGateTests 条带门同值）；1e-3 = 选定，只用来证明两口径分得开（锥形条带实测量级在报告里）。
//       覆盖：均匀步长直条、J 只沿 x；不覆盖：不等步长（不等步长下均匀 J 两口径也不同，是「每面各一半」约定的一部分）、二维 J、斜边与孔弧。
//       （2026-09-23 审查后补）读法：面导度 g = 调和平均(σt)·L/DistAB 恰好等于两段 DistAB/2 串联（ShellCurrent 的面导度）；把热按「各段电阻所在的格」记，
//       一维条带上逐格恰等于 ρJ²tA（内部面、压接面、孔面三类都如此）。所以锥形条带上 2.78 % 的逐格差是「½」相对网络自身串联分解的误差 ——
//       本门守的是「等于 ½ 的解析预测」，即**约定本身**，不是物理。怎么分（½／按厚度串联分／按形心距离分）是决 27 细则，【待决定】（实施记录 §7-2）。
//   门3 改回 ⇒ 改前逐位（快，整线）：接线门 1 的小算例（R48PropsWiringGateTests.QuickCase，纯铂、W08 两段、给定电流、耦合 2 轮），
//       设 LineCase.GateRevertFaceHeat 跑整线：结果里恰有一句改回说明（开关真的进了整线）；剔掉这一句后的去耗时全量转储 SHA-256 = F3 前的记录（接线门 1 在 8b90b5f 上的 Linux 记录）。
//       覆盖：LineRunner.Run 整条链（电流 → 热场 → 耦合 → 判据 → 文字）；不覆盖：保温搜索内层（InsulationSearch 另建算例，不带改回位）、Windows（没有记录）。
//       （2026-09-23 审查后补）靶 PreF3LinuxRecord = 「合并树去掉 F3 那一份 diff」上接线门 1 的 Linux SHA；现值只对 8b90b5f 成立。
//       凡是改接线门 1 转储的改动（本波已知：SEG → 8e0ffad1…、C3 → ffc61757…、RING → 01f51c89…；C4 实跑不动接线门 1〔合并 C4′ 后改：C4′（决 29 自适应）→ bc732f5d…，转储多 4 行记录（算例.MeshFineRadiusPlan 1 行、各片 Recipe.FineRadiusMm 3 行）、数值场不动〕）先合入，本门在合并树上就红（预期），
//       要按 PreF3LinuxRecord 注释重录靶；**严禁**用改回位在合并树上自取这个靶（那是自己比自己）。
//   门4 源码门（快）：Core 里调 ShellThermal.Solve／SolvePlateThermal 的地方（调用名与括号之间允许空白、换行），发热 J（命名实参 jHeatAPerMm2:／jMagAPerMm2: 优先，
//       否则第 2 个位置实参）不许是 .JMagAPerMm2 或 .JField（FlangeOut.JField 就是重构 J）；Core 里（ShellCurrent.cs 之外）不许再按
//       JMagAPerMm2[..] * …JMagAPerMm2[..] 自算 ρJ²。只跳过「返回类型 ShellThermalResult 紧跟方法名」的声明签名（同一行 => 后面的调用照扫）。
//       注入对照：改前写法 3 行 + 审查补的 4 种绕法（换行、一行 public static 包装、命名实参换顺序、.JField）必须报 7 条。
//       职责：给以后新加的调用点做**文本初筛**。现有 3 个生产入口另有行为级守护：LineRunner 整线 —— 接线门 1 记录 d95530d5… 与本门逐字钉住那一行；
//       InsulationSearch.SolvePoint —— 运行期 RecipeSelfCheck（抽热、盘峰、舌峰须与整线逐片解逐位相同，否则抛异常）；DesignScreen —— 门5。
//       不覆盖（文本门的固有局限）：发热实参经局部变量或别名转手（var jh = cur.JMagAPerMm2;）；jLocalAPerMm2 的取值（只在 LineRunner 那一行逐字钉住，
//       InsulationSearch 没传、缺省 null 退回发热 J —— 它不读局部热稳定）；经局部变量、Math.Pow 或跨行写的 ρJ²；ShellThermal.cs 内部不带类名前缀的调用；
//       Program.cs、UI、测试（决 26；测试侧清单见实施记录 §6）。
//   门6 局部热稳定（快）：逐格局部量用重构 J（jLocalAPerMm2）；R31.00／31.01／31.25 判决网格单片冻结解上报出的最小裕度不大于关口径最不稳格在开口径温度下的重算值；
//       注入「局部 J = 发热等效 J」（F3 第一版）⇒ 三例全挤占（孔边切格发热等效 J 虚高把预筛前 12 名占满）。
//   门5 DesignScreen 电阻因子（快）：面发热口径下 ShapeR 给出的电阻 = 离散网络电阻 ρ/CurrentInA（1e-6，同门1 出处）；改回 ⇒ 与改前算式（测试侧照抄）逐位相同。
//   归因（慢，只印不判）：W08 片0 18 例的单片冻结热解「开 − 关」；门 f 那 8 行整线（W08／W06 × 圆盘保温 10／20 × 判决／导航）「开 − 关」三条判据并列。
// ════════════════════════════════════════════════════════════════════════════
public class R48F3FaceHeatGateTests
{
    private readonly ITestOutputHelper _o;
    public R48F3FaceHeatGateTests(ITestOutputHelper o) { _o = o; }

    internal const string Sign = "2026-09-23，F3（C2）";
    internal const double ClosureTol = 1e-6;        // 门1／门5：选定值；生产 PCG 零初值下闭合按正交性只剩舍入（与收敛无关），见头注
    internal const double StripTol = 1e-9;          // 门2：选定（CG 1e-15 下的舍入余量）
    internal const double StripCgTol = 1e-15;
    internal const double TaperDiffMin = 1e-3;      // 门2：选定（证明分得开）

    /// <summary>
    /// 门3 的靶：接线门 1 在 F3 前（分支头 8b90b5f）的 Linux 记录（R48PropsWiringGateTests.LinuxRecord 在 F3 改动之前的值，§0.-20 重录的那个）。
    /// ★ 2026-09-23 审查后补：这个靶的定义是「合并树去掉 F3 那一份 diff」上接线门 1 的 Linux SHA，**现值只对 8b90b5f 成立**。
    ///   本波 SEG（→ 8e0ffad1…）、C3（→ ffc61757…）、RING（→ 01f51c89…）都改接线门 1 的转储（C4 实跑不动〔合并 C4′ 后改：C4′（决 29 自适应）→ bc732f5d…，转储多 4 行记录、数值场不动〕），它们中任一条与 F3 同在合并树上时，
    ///   改回位只撤 F3，SHA 回不到 fb2c3248… ⇒ 门3 红，属预期。重录：取「其余各条已合、F3 未合」的树（或合并树撤掉 F3 diff 的检出）跑接线门 1，
    ///   印出的 SHA 写进本常量，注释逐条写明变因（实际已合的 SEG／C3／RING）。**严禁**用改回位在合并树上自取（自己比自己，门3 就空了）。合并顺序归合并计划定。
    /// Windows：没有记录。取法：在 Windows 上用 F3 之前的树（8b90b5f，或合并树撤掉 F3 diff 的检出；接线门 1 注释里的剥离树做法跑出的也是这个数）跑接线门 1
    ///   （去耗时的同一份转储），印出的 SHA 就是 <see cref="PreF3WindowsRecord"/>。没填之前 Windows 快套件本门红，原因是没有记录。
    /// </summary>
    internal const string PreF3LinuxRecord = "fb2c32488eb4d3c408c6655cb544b856498a78174de21a2ff0726ca1e0083cf2";
    internal const string PreF3WindowsRecord = "";

    /// <summary>R48NMeshGateTests.门_带恒0_电功率闭合_导航判决 的 9 个几何（盘径五档 × 舌半宽 30，舌半宽四档 × 盘径 30）。</summary>
    internal static readonly (double R, double w)[] Geo9 =
    {
        (30.50, 30.0), (30.75, 30.0), (31.00, 30.0), (31.01, 30.0), (31.25, 30.0),
        (30.0, 29.50), (30.0, 29.55), (30.0, 29.70), (30.0, 29.80),
    };
    internal static readonly (string grade, double fine)[] Grades = { ("导航", 0.0), ("判决", 1.0) };

    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    private static bool SameArr(double[] a, double[] b) => a.Length == b.Length && Enumerable.Range(0, a.Length).All(i => Same(a[i], b[i]));
    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(s))).ToLowerInvariant();

    /// <summary>
    /// 2026-09-23 审查后加：证据档头印本次编译所用 F3 相关 Core 源码的指纹（五档按名排序、逐字节拼接的 SHA-256 前 16 位，读的是工作树源码），
    /// 让读档的人能认出档是哪一版代码跑的（整线归因第一次跑在 F3 第一版上、档里没写，审查 F3-T2／R1）。只作标识，不判。
    /// 注意：读的是**运行时的工作树源码**，镜像二进制若没重编，这个指纹与二进制对不上 —— 跑前按 tools/linux_mirror 重编。
    /// </summary>
    internal static string CoreSrcTag()
    {
        string core = Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core");
        var names = new[] { "DesignScreen.cs", "InsulationSearch.cs", "LineRunner.cs", "ShellCurrent.cs", "ShellThermal.cs" };
        using var ms = new MemoryStream();
        foreach (var n in names) { var b = File.ReadAllBytes(Path.Combine(core, n)); ms.Write(b, 0, b.Length); }
        return "Core 源码指纹（DesignScreen／InsulationSearch／LineRunner／ShellCurrent／ShellThermal）" + Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant()[..16]
             + "　局部热稳定口径：" + (File.ReadAllText(Path.Combine(core, "LineRunner.cs")).Contains("jLocalAPerMm2: cur.JMagAPerMm2", StringComparison.Ordinal) ? "终版（jLocalAPerMm2 = 重构 J）" : "**非终版**");
    }

    /// <summary>热场的发热式（ShellThermal：props.Rho(T)·1e3·J·J·t·A），温度取电流解的 T_解（等温 tC）。</summary>
    internal static double ThermalFormGen(ShellMesh m, double[] j, double tC)
    {
        var props = PtProps.Pure;
        double s = 0;
        for (int i = 0; i < m.CellCount; i++) s += props.Rho(tC) * 1e3 * j[i] * j[i] * m.Thickness[i] * m.Area[i];
        return s;
    }

    /// <summary>改回逐位：开关只换 HeatJAPerMm2 交哪个数组，其余逐位相同；改回时 HeatJAPerMm2 就是重构 J 数组本身。</summary>
    internal static bool RevertExact(ShellCurrentResult on, ShellCurrentResult off, out string why)
    {
        var w = new List<string>();
        if (!on.FaceHeat) w.Add("开的结果 FaceHeat = false");
        if (off.FaceHeat) w.Add("关的结果 FaceHeat = true");
        if (!ReferenceEquals(off.HeatJAPerMm2, off.JMagAPerMm2)) w.Add("关：HeatJAPerMm2 不是重构 J 数组本身");
        if (!SameArr(on.V, off.V)) w.Add("电位");
        if (!SameArr(on.JMagAPerMm2, off.JMagAPerMm2)) w.Add("重构 J");
        if (!SameArr(on.FaceGenW, off.FaceGenW)) w.Add("面发热");
        if (!Same(on.TotalGenW, off.TotalGenW)) w.Add("TotalGenW");
        if (!Same(on.CurrentInA, off.CurrentInA)) w.Add("归一化电流");
        if (!Same(on.JMaxAPerMm2, off.JMaxAPerMm2)) w.Add("JMax");
        why = string.Join("、", w);
        return w.Count == 0;
    }

    // ═══════════════════════════════════════════════ 门1 闭合 1e-6（18 例）
    [Fact]
    public void 门1_面发热闭合_W08片0_18例_1em6_改回逐位()
    {
        var p = new DesignInputs();
        var d0 = R48NMeshGateTests.Design("W08");
        double I = R48NMeshGateTests.PlateCurrentA, T = R48NMeshGateTests.PlateTempC;
        double rho = Materials.PtResistivity(T) * 1e3;
        string file = DeliverableOut.Stamped("R48_F3_门1_面发热闭合_W08片0_18例.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        var sw = Stopwatch.StartNew();
        W($"F3 门1 面发热闭合　W08 片0　等温 {I} A／{T} °C　18 例 = R48NMeshGateTests「带格与闭合」的 9 个几何 × 导航／判决");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}");
        W(CoreSrcTag());
        W($"门槛（跑前写死）：|新 P_gen/P_net − 1| ≤ {ClosureTol:0e0}（选定；Σ面 g·ΔV² = I_in − Σ自由格 V·r，生产 PCG 零初值下 Σ V·r 按正交性恒约 0 ⇒ 闭合只剩舍入、与收敛无关，收敛另由 Converged 判）；热场口径同；表达不了的发热 = 0；改回逐位。旧闭合 [0.99, 1.01] 只印（旧门在 R48NMeshGateTests，不收紧）。");
        W("P_net = I²·ρ/CurrentInA；旧 P_gen = Σ ρ·J重构²·t·A（TotalGenW）；新 P_gen = Σ 面发热（FaceGenTotalW）；热场口径 = Σ ρ(T解)·1e3·J等效²·t·A（ShellThermal 的式子）；残差界 = max|V|·Σ|r|/I_in（松界，只印；闭合 1e-13 不来自它）。");
        W("R\tw\t档\t单元\tP_net W\t旧P_gen W\t旧闭合\t新P_gen W\t新闭合−1\t残差界\t热场口径闭合−1\t新−旧 W\t|J等效/J重构−1|最大(J重构≥5%Jmax)\t该格(x,z)\t表达不了W\t改回逐位\t判读");
        int nBad = 0; var bad = new List<string>();
        foreach (var (R, w) in Geo9)
            foreach (var (grade, fine) in Grades)
            {
                var d = d0.Clone(); R48NMeshGateTests.SetRW(d, R, w);
                var (_, _, m, _) = R48NMeshGateTests.Build(d, p, fine);
                var on = ShellCurrent.Solve(m, I, rho, T);
                var off = ShellCurrent.Solve(m, I, rho, T, faceHeat: false);
                double pNet = I * I * rho / on.CurrentInA;
                double cOld = on.TotalGenW / pNet, cNew = on.FaceGenTotalW / pNet;
                double vMax = on.V.Max(v => Math.Abs(v));
                double resBound = Math.Max(1.0, vMax) * on.ResidualAbsSum / on.CurrentInA;
                double cTh = ThermalFormGen(m, on.HeatJAPerMm2, T) / pNet;
                double dj = 0; int dji = -1;
                for (int i = 0; i < m.CellCount; i++)
                {
                    if (!(on.JMagAPerMm2[i] >= 0.05 * on.JMaxAPerMm2)) continue;
                    double r = Math.Abs(on.HeatJAPerMm2[i] / on.JMagAPerMm2[i] - 1);
                    if (r > dj) { dj = r; dji = i; }
                }
                bool finite = on.HeatJAPerMm2.Length == m.CellCount && on.HeatJAPerMm2.All(v => double.IsFinite(v) && v >= 0);
                bool rev = RevertExact(on, off, out string why);
                var notes = new List<string>();
                if (!on.Converged) notes.Add("电流场未收敛");
                if (!(Math.Abs(cNew - 1) <= ClosureTol)) notes.Add($"新闭合 {cNew - 1:E3}");
                if (!(Math.Abs(cTh - 1) <= ClosureTol)) notes.Add($"热场口径闭合 {cTh - 1:E3}");
                if (!(on.FaceGenUnplacedW == 0)) notes.Add($"表达不了的发热 {on.FaceGenUnplacedW:E3} W");
                if (!finite) notes.Add("发热等效 J 有 NaN／负数");
                if (!rev) notes.Add("改回不逐位：" + why);
                string at = dji >= 0 ? $"({m.Centroid[dji].X:0.00},{m.Centroid[dji].Z:0.00})" : "—";
                W($"{R:0.00}\t{w:0.00}\t{grade}\t{m.CellCount}\t{pNet:0.0000}\t{on.TotalGenW:0.0000}\t{cOld:0.00000}\t{on.FaceGenTotalW:0.0000}\t{cNew - 1:E2}\t{resBound:E2}\t{cTh - 1:E2}\t{on.FaceGenTotalW - on.TotalGenW:+0.0000;-0.0000}\t{dj:0.0000}\t{at}\t{on.FaceGenUnplacedW:0}\t{(rev ? "是" : "**否**")}\t{(notes.Count == 0 ? "过" : "**" + string.Join("；", notes) + "**")}");
                if (notes.Count > 0) { nBad++; bad.Add($"R{R:0.00} w{w:0.00} {grade}：{string.Join("；", notes)}"); }
            }
        W($"── 不过 {nBad}／18　耗时 {sw.Elapsed.TotalSeconds:0} s　{Sign}");
        W("出处：网格 = R48NMeshGateTests.Build（LineRunner.PlateMeshAnalytic 生产配方）；电流 = ShellCurrent.Solve（开 = 缺省，关 = faceHeat: false）。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(nBad == 0, $"F3 门1 不过 {nBad}：{string.Join("　", bad)}（{file}）");
    }

    // ═══════════════════════════════════════════════ 门2 条带：均匀逐格相同、锥形逐格不同且等于解析预测
    private const int StripNx = 20;                 // x ∈ [0, 20] 步长 1
    private const double StripLc = 4.0;             // 压接段 [0, 4]
    private static readonly double[] StripZ = { 0, 2, 4 };

    internal static ShellMesh StripMesh(Func<int, double> thickOfCol)
    {
        var m = new ShellMesh { ClampFaceDirichlet = true, HoleFaceDirichlet = true };
        int nx = StripNx + 1, nz = StripZ.Length;
        var id = new int[nx, nz];
        for (int i = 0; i < nx; i++) for (int j = 0; j < nz; j++) { id[i, j] = m.Nodes.Count; m.Nodes.Add(new Vec3(i, 0, StripZ[j])); }
        for (int i = 0; i + 1 < nx; i++)
            for (int j = 0; j + 1 < nz; j++)
            {
                m.Cells.Add(new[] { id[i, j], id[i + 1, j], id[i + 1, j + 1], id[i, j + 1] });
                m.Area.Add(1.0 * (StripZ[j + 1] - StripZ[j]));
                m.Centroid.Add(new Vec3(i + 0.5, 0, 0.5 * (StripZ[j] + StripZ[j + 1])));
                m.Thickness.Add(thickOfCol(i)); m.Part.Add(0); m.Frac.Add(1.0);
            }
        m.BuildFaces(mid => Math.Abs(mid.X - StripNx) < 1e-9 ? ShellMesh.TagHole
                          : FlangeMesher.InClampSegment(mid.X, 0.0, StripLc, false) ? ShellMesh.TagTabEnd : ShellMesh.TagFree);
        m.ClampCell = Enumerable.Range(0, m.CellCount).Select(i => FlangeMesher.InClampSegment(m.Centroid[i].X, 0.0, StripLc, false)).ToArray();
        return m;
    }

    /// <summary>
    /// 解析预测：自由格 i（列号 c）面发热 ÷ ρJ²tA = t_c·(左 + 右)。步长 1、一维电流 ⇒ 每条面的电阻 ∝ 1/t_面（ρ·h/(W·t_面)），发热 = I_排²·电阻；
    /// ρJ²tA = I_排²·ρ·h/(W·t_c)。内部面 t_面 = 调和平均 2 t_a t_b/(t_a + t_b)、两侧各分一半 ⇒ ½·(t_a + t_b)/(2 t_a t_b)；
    /// 压接面、孔面只有半格距离、导度取自由格一侧 t_c、整份给自由格 ⇒ ½/t_c。
    /// </summary>
    private static double PredictRatio(int col, Func<int, double> t)
    {
        double tc = t(col);
        double Half(int a, int b) => 0.5 * (t(a) + t(b)) / (2 * t(a) * t(b));
        double left = col - 1 < (int)StripLc ? 0.5 / tc : Half(col - 1, col);
        double right = col + 1 >= StripNx ? 0.5 / tc : Half(col, col + 1);
        return tc * (left + right);
    }

    [Fact]
    public void 门2_条带_均匀逐格相同_锥形逐格不同且等于解析预测()
    {
        const double tRef = 1300;
        double rho = PtProps.Pure.Rho(tRef);
        var sb = new StringBuilder(); void W(string t = "") { sb.AppendLine(t); _o.WriteLine(t); }
        var cases = new (string name, Func<int, double> t)[] { ("均匀厚 1", _ => 1.0), ("锥形厚 1+0.25·列", c => 1.0 + 0.25 * c) };
        var fails = new List<string>();
        foreach (var (name, tf) in cases)
        {
            var m = StripMesh(tf);
            Assert.True(m.ClampFaceActive, "条带没进面上定电位");
            var on = ShellCurrent.Solve(m, 1.0, 1.0, tRef, null, 20000, StripCgTol);
            var off = ShellCurrent.Solve(m, 1.0, 1.0, tRef, null, 20000, StripCgTol, faceHeat: false);
            Assert.True(on.Converged && on.HoleFaceDirichlet, $"{name}：没收敛或孔面没生效");
            double maxSame = 0, maxPred = 0; int nFree = 0;
            for (int i = 0; i < m.CellCount; i++)
            {
                if (m.ClampCell[i])
                {
                    if (!(on.FaceGenW[i] == 0 && on.HeatJAPerMm2[i] == 0)) fails.Add($"{name}：压接格 {i} 面发热 {on.FaceGenW[i]:R}／等效 J {on.HeatJAPerMm2[i]:R} 不为 0");
                    continue;
                }
                nFree++;
                int col = (int)Math.Floor(m.Centroid[i].X);
                double e = rho * 1e3 * on.JMagAPerMm2[i] * on.JMagAPerMm2[i] * m.Thickness[i] * m.Area[i];
                double ratio = on.FaceGenW[i] / e;
                maxSame = Math.Max(maxSame, Math.Abs(ratio - 1));
                maxPred = Math.Max(maxPred, Math.Abs(ratio - PredictRatio(col, tf)));
            }
            double pNet = 1.0 * 1.0 * rho * 1e3 / on.CurrentInA;
            double clos = on.FaceGenTotalW / pNet - 1;
            bool rev = RevertExact(on, off, out string why);
            W($"{name}：自由格 {nFree}　逐格 |面发热/ρJ²tA − 1| 最大 {maxSame:E3}　对解析预测最大差 {maxPred:E3}　全片闭合 {clos:E2}　改回逐位 {(rev ? "是" : "否：" + why)}");
            if (!(maxPred <= StripTol)) fails.Add($"{name}：对解析预测 {maxPred:E3} > {StripTol:0e0}");
            if (!(Math.Abs(clos) <= StripTol)) fails.Add($"{name}：全片闭合 {clos:E3}");
            if (!rev) fails.Add($"{name}：改回不逐位 {why}");
            if (name.StartsWith("均匀") && !(maxSame <= StripTol)) fails.Add($"均匀条带两口径逐格不同 {maxSame:E3} > {StripTol:0e0}");
            if (name.StartsWith("锥形") && !(maxSame >= TaperDiffMin)) fails.Add($"锥形条带两口径逐格差 {maxSame:E3} < {TaperDiffMin:0e0}（门空转）");
        }
        Assert.True(fails.Count == 0, string.Join("；", fails));
    }

    // ═══════════════════════════════════════════════ 门3 改回 ⇒ 改前逐位（整线小算例）
    [Fact]
    public void 门3_改回逐位_整线小算例转储SHA回到F3前记录()
    {
        var lc = R48PropsWiringGateTests.QuickCase();
        lc.GateRevertFaceHeat = true;
        var sw = Stopwatch.StartNew();
        var r = LineRunner.Run(lc);
        sw.Stop();
        Assert.True(r.Ok, r.Message);
        int nNote = r.Notes.Count(n => n == LineRunner.FaceHeatRevertNote);
        Assert.True(nNote == 1, $"改回说明应恰有 1 句，实有 {nNote} —— 开关没进整线或重复写");
        r.Notes.RemoveAll(n => n == LineRunner.FaceHeatRevertNote);
        string sha = Sha(R48PropsWiringGateTests.DumpNoTiming(lc, r));
        bool win = OperatingSystem.IsWindows();
        string rec = win ? PreF3WindowsRecord : PreF3LinuxRecord;
        _o.WriteLine($"改回整线小算例：{sw.Elapsed.TotalSeconds:0.0} s　{(win ? "Windows" : "Linux")}　SHA-256 {sha}　F3 前记录 {(rec.Length == 0 ? "（无）" : rec)}");
        Assert.True(rec.Length > 0, $"本机（{(win ? "Windows" : "Linux")}）没有 F3 前记录（本机 SHA {sha} 只供核对）。取法见 PreF3WindowsRecord 注释。");
        Assert.True(rec == sha, $"改回后 SHA {sha} ≠ F3 前记录 {rec}。若本树已合入 SEG／C3／RING 等改接线门 1 转储的改动，本门红属预期：按 PreF3LinuxRecord 注释在「F3 未合」的树上重录靶，"
                              + "不许用改回位在本树上自取。否则是改回位没把 F3 撤干净。");
    }

    // ═══════════════════════════════════════════════ 门4 源码门
    /// <summary>取 <paramref name="s"/> 中 <paramref name="open"/> 处 '(' 起的顶层实参（按圆括号深度切逗号）。</summary>
    internal static List<string> TopArgs(string s, int open)
    {
        var args = new List<string>(); int depth = 0; var cur = new StringBuilder();
        for (int k = open; k < s.Length; k++)
        {
            char ch = s[k];
            if (ch == '(' || ch == '[' || ch == '{') { if (depth++ > 0) cur.Append(ch); continue; }
            if (ch == ')' || ch == ']' || ch == '}') { if (--depth == 0) { args.Add(cur.ToString().Trim()); break; } cur.Append(ch); continue; }
            if (ch == ',' && depth == 1) { args.Add(cur.ToString().Trim()); cur.Clear(); continue; }
            cur.Append(ch);
        }
        return args;
    }

    /// <summary>热场调用名（2026-09-23 审查后：调用名与括号之间允许空白、换行）。</summary>
    private static readonly Regex HeatCallRe = new(@"(ShellThermal\s*\.\s*Solve|SolvePlateThermal)\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// 扫一份源码：热场调用的发热实参（命名实参 jHeatAPerMm2:／jMagAPerMm2: 优先，否则第 2 个位置实参）是 .JMagAPerMm2 或 .JField ⇒ 报；
    /// 按重构 J 自算 ρJ²（同一行两个 JMagAPerMm2[..] 相乘）⇒ 报。注释行跳过；只跳过「返回类型 ShellThermalResult 紧跟方法名」的声明签名。
    /// 2026-09-23 审查后改：原先按「本行含 public static／internal static」整行跳过，一行写成的包装（=> ShellThermal.Solve(…)）因此漏掉；调用名原先逐字匹配，换行即漏。
    /// </summary>
    internal static List<string> ScanHeatJ(string name, string s, out int calls)
    {
        var bad = new List<string>(); calls = 0;
        foreach (Match mt in HeatCallRe.Matches(s))
        {
            int i = mt.Index;
            int lineStart = s.LastIndexOf('\n', Math.Max(0, i - 1)) + 1;
            string head = s.Substring(lineStart, i - lineStart);
            if (head.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(head, @"\bShellThermalResult\s+$")) continue;     // 声明签名本身（LineRunner.SolvePlateThermal 的声明）
            string call = Regex.Replace(mt.Value, @"\s+", "");
            var a = TopArgs(s, mt.Index + mt.Length - 1);
            calls++;
            if (a.Count < 2) { bad.Add($"{name}：{call} 实参解析不出（{a.Count} 个）"); continue; }
            string heat = a.FirstOrDefault(x => Regex.IsMatch(x, @"^(jHeatAPerMm2|jMagAPerMm2)\s*:")) ?? a[1];
            if (Regex.IsMatch(heat, @"\.(JMagAPerMm2|JField)\b")) bad.Add($"{name}：{call}{a[0]}, … {heat} …）—— 发热 J 传的是重构 J");
        }
        if (name != "ShellCurrent.cs")
            foreach (var (line, no) in s.Split('\n').Select((l, k) => (l, k + 1)))
            {
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (Regex.IsMatch(line, @"JMagAPerMm2\[[^\]]*\]\s*\*\s*[\w\.]*JMagAPerMm2\[")) bad.Add($"{name}:{no}：按重构 J 自算 ρJ² —— {line.Trim()}");
            }
        return bad;
    }

    [Fact]
    public void 门4_源码门_Core不许把重构J当发热J传进热场()
    {
        string core = Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core");
        var bad = new List<string>(); int calls = 0;
        foreach (string f in Directory.GetFiles(core, "*.cs"))
        {
            bad.AddRange(ScanHeatJ(Path.GetFileName(f), File.ReadAllText(f), out int c));
            calls += c;
        }
        _o.WriteLine($"Core 里热场调用 {calls} 处");
        Assert.True(calls >= 3, $"热场调用只找到 {calls} 处（应 ≥ 3：RunOnce 的 Thermal、SolvePlateThermal 本体、InsulationSearch.SolvePoint）—— 门空转");
        Assert.True(bad.Count == 0, "Core 里有地方仍按重构 J 算发热：" + string.Join("；", bad));
        // 整线逐片热解（RunOnce 的 Thermal）：发热传发热等效 J、逐格局部量传重构 J —— 两个都要在（门6 讲为什么局部量不许用发热等效 J）
        string lr = File.ReadAllText(Path.Combine(core, "LineRunner.cs"));
        Assert.Contains("SolvePlateThermal(mesh, cur.HeatJAPerMm2, tRoot, ts, jLocalAPerMm2: cur.JMagAPerMm2)", lr);
        // 注入对照（改前写法，逐字取自 F3 前的 LineRunner.cs／InsulationSearch.cs／DesignScreen.cs）：必须报
        string inj = "            ShellThermalResult Thermal(ShellCurrentResult cur) => SolvePlateThermal(mesh, cur.JMagAPerMm2, tRoot, ts);\n"
                   + "            var th = LineRunner.SolvePlateThermal(mesh, cur.JMagAPerMm2, tRoot, ts);\n"
                   + "            gen += rhoMm * sc.JMagAPerMm2[i] * sc.JMagAPerMm2[i] * mesh.Thickness[i] * mesh.Area[i];\n"
                   // 2026-09-23 审查补的 4 种绕法（审查探针 D、E、C、F），也必须报
                   + "            var thD = ShellThermal.Solve\n                (mesh, cur.JMagAPerMm2, p2, tRoot, insulX);\n"
                   + "    public static ShellThermalResult WrapE(ShellMesh m, ShellCurrentResult c) => ShellThermal.Solve(m, c.JMagAPerMm2, p2, 1150.0, double.NaN);\n"
                   + "            var thC = LineRunner.SolvePlateThermal(mesh, tRootC: tRoot, s: ts, jHeatAPerMm2: cur.JMagAPerMm2);\n"
                   + "            var thF = ShellThermal.Solve(f.Mesh, f.JField, p2, f.TRootC, double.NaN);\n";
        var got = ScanHeatJ("注入", inj, out int ci);
        _o.WriteLine("注入对照：" + string.Join("；", got));
        Assert.Equal(6, ci);
        Assert.Equal(7, got.Count);
        // 声明签名本身不计、不报（同一扫描器，生产声明的写法）
        var decl = ScanHeatJ("声明", "    public static ShellThermalResult SolvePlateThermal(ShellMesh mesh, double[] jHeatAPerMm2, double tRootC, PlateThermalSetup s)\n", out int cd);
        Assert.True(cd == 0 && decl.Count == 0, $"声明签名被当成调用：计 {cd}，报 {decl.Count}");
    }

    // ═══════════════════════════════════════════════ 门5 DesignScreen 电阻因子
    [Fact]
    public void 门5_DesignScreen电阻因子_面发热等于网络电阻_改回同改前算式()
    {
        var p = new DesignInputs();
        var d = R48NMeshGateTests.Design("W08");
        var (_, g, m, _) = R48NMeshGateTests.Build(d, p, 0);
        const double iRef = 1000.0, tRef = 1050.0;
        double tanX = g.Tangent().X;
        var sOn = DesignScreen.Extract(m, iRef, tRef, tanX);
        var sOff = DesignScreen.Extract(m, iRef, tRef, tanX, faceHeat: false);
        // 改前算式（照抄 F3 前 DesignScreen.Extract）：ρ = Materials.PtResistivity(refTempC)·1e3，gen = Σ ρ·J重构²·t·A，ShapeR = gen/I²·tRef/ρ
        double rhoMm = Materials.PtResistivity(tRef) * 1e3;
        var sc = ShellCurrent.Solve(m, iRef, rhoMm, tRef);
        double tMean = m.VolumeMm3 / Math.Max(1e-9, m.TotalArea);
        double gen = 0;
        for (int i = 0; i < m.CellCount; i++) gen += rhoMm * sc.JMagAPerMm2[i] * sc.JMagAPerMm2[i] * m.Thickness[i] * m.Area[i];
        double shapeROld = gen / (iRef * iRef) * tMean / rhoMm;
        double rNet = rhoMm / sc.CurrentInA;                     // 离散网络电阻 Ω
        double rOn = sOn.ShapeR * rhoMm / tMean;                  // 面发热口径给出的电阻
        _o.WriteLine($"ShapeR 开 {sOn.ShapeR:R}　关 {sOff.ShapeR:R}　改前算式 {shapeROld:R}　网络电阻 {rNet * 1e6:0.000000} µΩ　开给出 {rOn * 1e6:0.000000} µΩ（相对差 {rOn / rNet - 1:E2}）　关给出 {sOff.ShapeR * rhoMm / tMean * 1e6:0.000000} µΩ");
        Assert.True(Same(sOff.ShapeR, shapeROld), $"改回 ShapeR {sOff.ShapeR:R} ≠ 改前算式 {shapeROld:R}");
        Assert.True(Same(sOff.ShapeJ, sOn.ShapeJ) && Same(sOff.ShapeJMean, sOn.ShapeJMean), "ShapeJ／ShapeJMean 应与发热口径无关（用重构 J）");
        Assert.True(Math.Abs(rOn / rNet - 1) <= ClosureTol, $"面发热口径的电阻 {rOn:R} 对网络电阻 {rNet:R} 相对差 {rOn / rNet - 1:E3} > {ClosureTol:0e0}");
    }

    // ═══════════════════════════════════════════════ 门6 局部热稳定用重构 J：报出的最小裕度不被孔边切格挤占
    /// <summary>
    /// 片0 判决网格三个几何（R31.00／31.01／31.25 × w30，首跑前在临时探针上看到挤占的那三个）：单片冻结解（管根 1150 °C，PlateThermalInputs 生产组装）。
    /// 关口径（热场吃重构 J）报出的最不稳格 i₀（按温度与半径逐位认格），在开口径的温度场下用同一个 LocalStability.Check、同一横向导热长与保温重算它的裕度 m₀；
    /// 开口径报出的最小裕度必须 ≤ m₀ + 1e-9（报出的是「最小」，不许比一个没被评的格还大）。
    /// 注入对照：把发热等效 J 当局部 J 传（jLocalAPerMm2 = HeatJAPerMm2，F3 第一版的写法）⇒ 必须违反（报 9.886／17.294／19.419 对 7.399／7.355／7.460，首跑探针）。
    /// 阈值 1e-9：选定（同一函数同一输入的舍入余量）。覆盖：这三个几何的单片冻结解；不覆盖：整线、导航网格（导航网格上首跑未见挤占）、候选预筛本身前 12 名的规则。
    /// </summary>
    [Fact]
    public void 门6_局部热稳定用重构J_报出最小裕度不被孔边切格挤占_注入发热等效J则红()
    {
        var p = new DesignInputs();
        var d0 = R48NMeshGateTests.Design("W08");
        double I = R48NMeshGateTests.PlateCurrentA, T = R48NMeshGateTests.PlateTempC;
        double rho = Materials.PtResistivity(T) * 1e3;
        var fails = new List<string>(); int injRed = 0;
        foreach (double R in new[] { 31.00, 31.01, 31.25 })
        {
            var d = d0.Clone(); R48NMeshGateTests.SetRW(d, R, 30.0);
            var (lc, _, m, _) = R48NMeshGateTests.Build(d, p, 1.0);
            var on = ShellCurrent.Solve(m, I, rho, T);
            var ts = LineRunner.PlateThermalInputs(lc, 0, I, null);
            var thOff = LineRunner.SolvePlateThermal(m, on.JMagAPerMm2, T, ts);
            int i0 = Enumerable.Range(0, m.CellCount).First(i => Same(thOff.T[i], thOff.LocalStabTempC)
                        && Math.Abs(Math.Sqrt(m.Centroid[i].X * m.Centroid[i].X + m.Centroid[i].Z * m.Centroid[i].Z) - thOff.LocalStabRMm) < 1e-9);
            double[] insTry = { ts.P2.FlangeInsulThickMm, double.IsNaN(ts.TabInsulThickMm) ? 0 : ts.TabInsulThickMm, 0.0 };
            double ins = insTry.First(x => Same(LocalStability.Check(ts.P2, thOff.T[i0], on.JMagAPerMm2[i0], m.Thickness[i0], x, thOff.LocalStabLatLenMm).Margin, thOff.LocalStabMargin));
            foreach (bool inject in new[] { false, true })
            {
                var th = LineRunner.SolvePlateThermal(m, on.HeatJAPerMm2, T, ts, jLocalAPerMm2: inject ? on.HeatJAPerMm2 : on.JMagAPerMm2);
                double m0 = LocalStability.Check(ts.P2, th.T[i0], on.JMagAPerMm2[i0], m.Thickness[i0], ins, thOff.LocalStabLatLenMm).Margin;
                bool ok = th.LocalStabMargin <= m0 + 1e-9;
                _o.WriteLine($"R{R:0.00} 判决 {(inject ? "注入（局部 J = 发热等效 J）" : "生产（局部 J = 重构 J）")}：报出最小裕度 {th.LocalStabMargin:0.000}（r {th.LocalStabRMm:0.000}，J {th.LocalStabJAPerMm2:0.000}）　关口径最不稳格 r {thOff.LocalStabRMm:0.000} 在本温度场下重算 {m0:0.000}（关报出 {thOff.LocalStabMargin:0.000}）　{(ok ? "不挤占" : "**挤占**")}");
                if (!inject && !ok) fails.Add($"R{R:0.00}：报出 {th.LocalStabMargin:0.000} > 未被评的格 {m0:0.000}");
                if (inject && !ok) injRed++;
            }
        }
        Assert.True(fails.Count == 0, "局部热稳定报出的最小裕度被挤占：" + string.Join("；", fails));
        Assert.True(injRed == 3, $"注入发热等效 J 应三例全挤占（门不空转），实 {injRed}");
    }

    // ═══════════════════════════════════════════════ 归因（慢，只印不判）
    /// <summary>W08 片0 18 例：等温电流 + 单片冻结热解（管根 1150 °C、PlateThermalInputs 生产组装），开 − 关。不是判据值（判据要整线），是热场对发热口径的直接响应。</summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 归因_W08片0_18例_单片冻结热解_开减关()
    {
        var p = new DesignInputs();
        var d0 = R48NMeshGateTests.Design("W08");
        double I = R48NMeshGateTests.PlateCurrentA, T = R48NMeshGateTests.PlateTempC;
        double rho = Materials.PtResistivity(T) * 1e3;
        string file = DeliverableOut.Stamped("R48_F3_归因_W08片0_18例_单片冻结热解_开减关.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        var sw = Stopwatch.StartNew();
        W($"F3 归因（只印不判）　W08 片0　等温电流 {I} A／{T} °C　单片冻结热解：管根 {T} °C，保温／铜排／分区按 LineRunner.PlateThermalInputs（本几何的整线算例）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}");
        W(CoreSrcTag());
        W("开 = 生产（热场发热吃发热等效 J；盘／舌峰处报出的 J 与局部热稳定用重构 J）；关 = 改回（热场全吃重构 J）。不是三条硬判据（那要整线，见整线归因档），是同一片板在同一边界下对发热口径的响应。");
        W("R\tw\t档\t单元\t发热 开\t发热 关\tΔ发热W\t抽热 开\t抽热 关\tΔ抽热W\t最高温 开\tΔ最高温K\t盘区峰 开\tΔ盘区峰K\t舌区峰 开\tΔ舌区峰K\t热稳定裕度 开\t关\t热稳定报出J 开\t关\t耗时s");
        var zb = new StringBuilder();   // 2026-09-23 审查后加（P3）：分区拆账 —— 把「总量变化」和「分布变化」拆开
        string[] zones = { "压接格", "压接邻格", "孔边格", "孔边+1格带", "盘其余", "舌其余" };
        foreach (var (R, w) in Geo9)
            foreach (var (grade, fine) in Grades)
            {
                var s1 = Stopwatch.StartNew();
                var d = d0.Clone(); R48NMeshGateTests.SetRW(d, R, w);
                var (lc, g, m, _) = R48NMeshGateTests.Build(d, p, fine);
                var on = ShellCurrent.Solve(m, I, rho, T);
                var off = ShellCurrent.Solve(m, I, rho, T, faceHeat: false);
                var ts = LineRunner.PlateThermalInputs(lc, 0, I, null);
                var thOn = LineRunner.SolvePlateThermal(m, on.HeatJAPerMm2, T, ts, jLocalAPerMm2: on.JMagAPerMm2);
                var thOff = LineRunner.SolvePlateThermal(m, off.HeatJAPerMm2, T, ts);
                string cv = thOn.Converged && thOff.Converged ? "" : "（热场未收敛）";
                W($"{R:0.00}\t{w:0.00}\t{grade}\t{m.CellCount}\t{thOn.QGenW:0.000}\t{thOff.QGenW:0.000}\t{thOn.QGenW - thOff.QGenW:+0.000;-0.000}\t{thOn.QFromTubeW:0.0000}\t{thOff.QFromTubeW:0.0000}\t{thOn.QFromTubeW - thOff.QFromTubeW:+0.0000;-0.0000}"
                  + $"\t{thOn.TMaxC:0.000}\t{thOn.TMaxC - thOff.TMaxC:+0.000;-0.000}\t{thOn.TDiscMaxC:0.000}\t{thOn.TDiscMaxC - thOff.TDiscMaxC:+0.000;-0.000}\t{thOn.TTabMaxC:0.000}\t{thOn.TTabMaxC - thOff.TTabMaxC:+0.000;-0.000}"
                  + $"\t{thOn.LocalStabMargin:0.000}\t{thOff.LocalStabMargin:0.000}\t{thOn.LocalStabJAPerMm2:0.000}\t{thOff.LocalStabJAPerMm2:0.000}\t{s1.Elapsed.TotalSeconds:0.0}{cv}");
                // ── 分区拆账（P3）
                int n = m.CellCount; double discR = g.DiscRadiusMm, holeR = m.HoleRadiusMm, band = fine > 0 ? fine : 2.0;
                var clamp = m.ClampCell.Length == n ? m.ClampCell : new bool[n];
                var hole = new bool[n]; var clampNbr = new bool[n];
                foreach (var fc in m.Faces)
                {
                    if (fc.B < 0) { if (fc.Tag == ShellMesh.TagHole) hole[fc.A] = true; continue; }
                    if (clamp[fc.A] && !clamp[fc.B]) clampNbr[fc.B] = true;
                    if (clamp[fc.B] && !clamp[fc.A]) clampNbr[fc.A] = true;
                }
                double Rad(int i) => Math.Sqrt(m.Centroid[i].X * m.Centroid[i].X + m.Centroid[i].Z * m.Centroid[i].Z);
                string Zone(int i) => clamp[i] ? zones[0] : clampNbr[i] ? zones[1] : hole[i] ? zones[2] : Rad(i) <= holeR + band + 1e-9 ? zones[3] : Rad(i) <= discR ? zones[4] : zones[5];
                var props = PtProps.For(ts.P2);
                bool accounted(int i) => thOff.BoundaryCell.Length != n || !thOff.BoundaryCell[i];
                // 关温度场下的开口径发热（去掉温度反馈，只剩发热口径本身）：ρ(T_关)·J等效²·t·A，入账格同 CellGenW
                double GenAtOffT(int i) => accounted(i) ? props.Rho(thOff.T[i]) * 1e3 * on.HeatJAPerMm2[i] * on.HeatJAPerMm2[i] * m.Thickness[i] * m.Area[i] : 0;
                zb.AppendLine($"{R:0.00}\t{w:0.00}\t{grade}\t全片\t{n}\t{on.FaceGenTotalW:0.000}\t{on.TotalGenW:0.000}\t{on.FaceGenTotalW - on.TotalGenW:+0.000;-0.000}"
                            + $"\t{thOn.QGenW:0.000}\t{thOff.QGenW:0.000}\t{thOn.QGenW - thOff.QGenW:+0.000;-0.000}\t{Enumerable.Range(0, n).Sum(GenAtOffT) - thOff.QGenW:+0.000;-0.000}"
                            + $"\t\t\t{thOn.QToClampW:0.000}\t{thOff.QToClampW:0.000}\t{thOn.QToClampW - thOff.QToClampW:+0.000;-0.000}"
                            + $"\t{Enumerable.Range(0, n).Sum(i => thOn.CellGenW[i] * Rad(i)) / thOn.QGenW - Enumerable.Range(0, n).Sum(i => thOff.CellGenW[i] * Rad(i)) / thOff.QGenW:+0.0000;-0.0000}");
                foreach (var z in zones)
                {
                    var ids = Enumerable.Range(0, n).Where(i => Zone(i) == z).ToArray();
                    double qOn = ids.Sum(i => on.FaceGenW[i]), qOff = ids.Sum(i => rho * on.JMagAPerMm2[i] * on.JMagAPerMm2[i] * m.Thickness[i] * m.Area[i]);
                    double gOn = ids.Sum(i => thOn.CellGenW[i]), gOff = ids.Sum(i => thOff.CellGenW[i]), gOnOffT = ids.Sum(GenAtOffT);
                    double tOn = ids.Length > 0 ? ids.Average(i => thOn.T[i]) : double.NaN, tOff = ids.Length > 0 ? ids.Average(i => thOff.T[i]) : double.NaN;
                    zb.AppendLine($"{R:0.00}\t{w:0.00}\t{grade}\t{z}\t{ids.Length}\t{qOn:0.000}\t{qOff:0.000}\t{qOn - qOff:+0.000;-0.000}\t{gOn:0.000}\t{gOff:0.000}\t{gOn - gOff:+0.000;-0.000}\t{gOnOffT - gOff:+0.000;-0.000}"
                                + $"\t{tOn:0.0}\t{tOff:0.0}\t\t\t\t");
                }
            }
        W(); W("── 分区拆账（2026-09-23 审查后加，P3）：等温 = 电流解温度下（开 = 面发热 FaceGenW，关 = ρJ重构²tA）；热场 = 热解后 CellGenW（ρ(T_解)），只计入账格；");
        W("   「关温度场下开口径 − 关」= 用关的温度场评开口径发热减关的发热（去掉温度反馈，只剩发热口径与分布的作用）；QToClampW = 流进铜排的热（开／关）；");
        W("   发热加权半径 Δ = Σ CellGen·r/QGen 开 − 关（mm，负 = 热源往里挪）。分区：压接格 → 压接邻格（与压接格共面）→ 孔边格（有孔面）→ 孔边+1格带（r ≤ 孔半径 + 一格：判决 1 mm、导航 2 mm）→ 盘其余（r ≤ 盘半径）→ 舌其余。");
        W("R\tw\t档\t区\t格数\t等温 开\t等温 关\tΔ等温W\t热场 开\t热场 关\tΔ热场W\t关温度场下开口径−关W\t平均T开\t平均T关\tQToClamp 开\t关\tΔW\t发热加权半径Δmm");
        sb.Append(zb);
        W($"── 总耗时 {sw.Elapsed.TotalSeconds:0} s　{Sign}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
    }

    /// <summary>门 f 那 8 行（每设计 4 行：圆盘保温 10／20 × 判决／导航）整线解，开（生产）与关（LineCase.GateRevertFaceHeat）并列，三条判据 + 片0 发热／抽热。只印不判。</summary>
    [Trait("速度", "慢")]
    [Theory]
    [InlineData("W08")]
    [InlineData("W06")]
    public void 归因_门f八行整线_开减关(string which)
    {
        var d0 = R48NMeshGateTests.Design(which);
        int par = int.TryParse(Environment.GetEnvironmentVariable("F3_ATTR_PAR"), out int pp) && pp > 0 ? pp : 2;
        string file = DeliverableOut.Stamped($"R48_F3_归因_门f整线_{which}_开减关.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        var sw = Stopwatch.StartNew();
        W($"F3 归因（只印不判）　{which}（{d0.Name}）　门 f 的 4 行（圆盘保温 10／20 mm × 判决 MeshVerify.RequiredMeshFor／导航）× 开（生产，面发热）／关（改回，LineCase.GateRevertFaceHeat）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}　并发 {par}（与别的长跑同机，耗时只作参考）");
        W(CoreSrcTag());
        W($"基准设计：{(which == "W08" ? R48LW08NavDesign.Source : R48LW06FineDesign.Source)}");
        var reqFine = MeshVerify.RequiredFineMmFor(d0);   // （合并 C4′ 时改，变因 = 决 29 自适应：RequiredMeshFor 签名加工艺参数 DesignInputs，细区半径改为计划初值 max(盘半径, 孔半径) + 热长度，W08 53.697 mm；细步单独取 RequiredFineMmFor）
        var jobs = new List<(string tag, DesignSpec d, double fine, string grade, bool off)>();
        foreach (double disc in new[] { 10.0, 20.0 })
            foreach (var (grade, fine) in new[] { ("判决", reqFine), ("导航", 0.0) })
                foreach (bool off in new[] { false, true })
                {
                    var d = d0.Clone();
                    d.FlangeInsulated = true; d.FlangeInsulMm = disc;
                    d.DiscInsulMm = Array.Empty<double>();
                    jobs.Add(($"盘{disc:0}", d, fine, grade, off));
                }
        var p = new DesignInputs();
        var rows = new R48NMeshGateTests.LineRow[jobs.Count];
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = par }, k =>
        {
            var j = jobs[k];
            rows[k] = R48NMeshGateTests.SolveLine(j.tag, j.d, p, j.fine, j.d.DiscRadiusMm, j.d.TabHalfWidthMm, j.grade,
                                                  caseTweak: j.off ? (Action<LineCase>)(lc => lc.GateRevertFaceHeat = true) : null);
        });
        W(); W("── 开（生产：面发热）"); W(R48NMeshGateTests.LineHead);
        for (int k = 0; k < jobs.Count; k++) if (!jobs[k].off) W(rows[k].Text);
        W(); W("── 关（改回：重构 J 的 ρJ²tA，= F3 前口径；门3 证整线逐位）"); W(R48NMeshGateTests.LineHead);
        for (int k = 0; k < jobs.Count; k++) if (jobs[k].off) W(rows[k].Text);
        W(); W("── 开 − 关");
        W("标签\t档\t最热铂高出热偶读数 开\t关\tΔK\t管根低于热偶读数 开\t关\tΔK\t管孔净流入 开\t关\tΔW\t片0发热 开\t关\tΔW\t片0抽热 开\t关\tΔW\t耦合轮 开／关\t耗时 开／关 s");
        for (int k = 0; k < jobs.Count; k++)
        {
            if (jobs[k].off) continue;
            int ko = k + 1;   // 同一标签、同一档的关紧跟在开后面
            var a = rows[k]; var b = rows[ko];
            if (!(a.Ok && a.Converged && b.Ok && b.Converged)) { W($"{a.Tag}\t{a.Grade}\t判不了：开 {a.Message}／关 {b.Message}"); continue; }
            W($"{a.Tag}\t{a.Grade}\t{a.Hot:0.000}\t{b.Hot:0.000}\t{a.Hot - b.Hot:+0.000;-0.000}\t{a.Cold:0.000}\t{b.Cold:0.000}\t{a.Cold - b.Cold:+0.000;-0.000}\t{a.Flux:0.000}\t{b.Flux:0.000}\t{a.Flux - b.Flux:+0.000;-0.000}"
              + $"\t{a.QGen0:0.000}\t{b.QGen0:0.000}\t{a.QGen0 - b.QGen0:+0.000;-0.000}\t{a.QTube0:0.000}\t{b.QTube0:0.000}\t{a.QTube0 - b.QTube0:+0.000;-0.000}\t{a.Rounds}／{b.Rounds}\t{a.Sec:0}／{b.Sec:0}");
        }
        // 2026-09-23 审查后加（P3）：逐片抽热与发热，开／关／Δ；「管孔净流入」判据取各片中最小的那一片（LineRunner：worstFlux = flanges.OrderBy(QFromTubeW).First()），标 ★
        W(); W("── 逐片（开 − 关）：抽热 = FlangeOut.QFromTubeW（>0 = 从管抽热），发热 = QGenW；★ = 该档净流入判据取的那一片（开／关各标）");
        W("标签\t档\t片\t抽热 开\t关\tΔW\t发热 开\t关\tΔW\t判据片");
        for (int k = 0; k < jobs.Count; k++)
        {
            if (jobs[k].off) continue;
            var a = rows[k]; var b = rows[k + 1];
            if (!(a.Ok && b.Ok) || a.PlateQTube.Length != b.PlateQTube.Length) continue;
            int wa = Array.IndexOf(a.PlateQTube, a.PlateQTube.Min()), wb = Array.IndexOf(b.PlateQTube, b.PlateQTube.Min());
            for (int jp = 0; jp < a.PlateQTube.Length; jp++)
                W($"{a.Tag}\t{a.Grade}\t{jp} {a.PlateNames[jp]}\t{a.PlateQTube[jp]:0.000}\t{b.PlateQTube[jp]:0.000}\t{a.PlateQTube[jp] - b.PlateQTube[jp]:+0.000;-0.000}"
                  + $"\t{a.PlateQGen[jp]:0.000}\t{b.PlateQGen[jp]:0.000}\t{a.PlateQGen[jp] - b.PlateQGen[jp]:+0.000;-0.000}\t{(jp == wa ? "★开" : "")}{(jp == wb ? "★关" : "")}");
        }
        W($"── 总耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟　{Sign}");
        W("出处：整线解 = LineRunner.Run（经 R48NMeshGateTests.SolveLine，caseTweak 设改回位）；读数 = LineResult.ValueOf；逐片 = LineResult.Flanges。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(rows.All(r => r.Ok), "有一档没解出：" + string.Join("；", rows.Where(r => !r.Ok).Select(r => r.Text)));
    }
}
