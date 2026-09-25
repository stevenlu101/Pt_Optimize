using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  F6（2026-09-23）：孔边电流场改为**孔面上定电位**（ShellMesh.HoleFaceDirichlet）+ 弧面距离取形心到孔圆的法向距（ShellMesh.BoundaryDistMm）
//  + 孔面只认弧面（MeshRules.HoleTagArcOnly）+ 局部热稳定的管孔锚点在孔圆上（ShellThermal holeAnchorOnCircle）。机理与改动见 HANDOVER F6 一节。
//
//  门槛跑前写死。出处逐条写明 —— 沿用已有的、由数学推出的、「选定」的（选定 = 没有推导，是定的；写明理由）：
//    门_电极位置随格集合的跳幅_不超门b阈值：相隔 0.01 的 |Δe| ≤ R48NMeshGateTests.RStepTolPct（沿用：门 b 原阈值与原间距 0.2 %／0.01，按 0.001 取样把 10 种相位全判）。
//    门_电流守恒_面上：
//      · 恒等式 Σ_自由格 r_i = I_in − I_out 只允许舍入 1e-12·I_in（1e-12 选定：舍入余量，实测 ≤ 4.6e-16，不是推导）；Cons ≤ Σ|r_i|/I_in（三角不等式，同一舍入余量）；
//        测试侧复算 Σ t·L·V/d 与 CurrentOutA 相对差 ≤ 1e-12（选定：同一个舍入余量）。⚠ 这三条**只证装配一致（in／out／残差三处簿记），不证物理**：
//        恒等式对任意 V 都成立，复算出流与 ShellCurrent 是同一个算式（findings #1／#25／#68）。
//      · 独立一项（F6 审查后加）：重构出的 J 在孔面上积分 Σ (J·n̂)·弦长·t 与 CurrentOutA（面导度 Σ g·V）相对差 ≤ 5 %
//        —— 5 % 借用 R48NSliverGateTests.ScaleSpreadTol（同为「J 重构的离散误差容许量」；借用，不是推导），跑前定、不随实测调。
//      · 0 ≤ V ≤ 1：在 tol = 1e-13 的重解上判 [−1e-12, 1 + 1e-12]（1e-13 沿用 门_双舌板孔边对称 的收紧容差；1e-12 选定的舍入余量）。生产 tol = 1e-9 下的 Vmin 只印 ——
//        离散极值原理只对精确离散解成立，CG 迭代解差 A⁻¹r、‖r‖∞ ≤ tol·‖b‖∞，‖A⁻¹‖∞ 门内拿不到、推不出严格界（判决网格在生产 tol 下 Vmin ≈ −8e-11，findings #2）。
//    门_弧面距离_单点通量误差界：精确径向解 V = ln(|C|/rh) 代入每条弧面，两点通量 ÷ 精确通量 = ln(1+x)/x ∈ [1 − x/2, 1]，x = d/rh —— 由 ln 的泰勒界推出，容差 1e-12（选定舍入余量）。
//      ⚠ d = |C| − rh 时比值恒等于 ln(1+x)/x，法向距那一支按构造必过（findings #3／#13／#34）；有牙的是「改回直线距必须红」那一支。
//    门_孔边J峰随加密收敛：三档 JMax 极差 ≤ R48NSliverGateTests.ScaleSpreadTol（沿用，直接引用常量）。
//    门_孔面只在孔圆上：所有管孔面有有限的弧半径且中点离孔圆 ≤ 1e-9 mm（选定：舍入余量；弧中点按 rh·(cos, sin) 建，实测 ≤ 1e-14 量级）。
//    门_双舌板孔边对称：镜像差 ≤ 1e-9（借用条带门的 1e-9 —— 条带门那是相对误差，这里套在 V（0…1）、J（A/mm²）与相对出流差上，是借用、选定，不是推导）。
//  「改回 ⇒ 红」对照（F6 审查后照实改写，findings #21／#45／#66）：6 道里 **4 道带改回对照**（电极位置、弧面距离、J 峰、孔面只在孔圆上，注射 MeshRules 开关）；
//    门_电流守恒_面上 只有「J 积分」那一项带改回对照（改回整格钉时孔格 J 不含孔面通量），恒等式三条与 V 界没有（恒等式对任意 V 成立；改回整格钉同样 0 ≤ V ≤ 1）；
//    门_双舌板孔边对称 **无改回对照**：改回整格钉在对称几何上同样给出对称的场，没有一个开关能让它红（要红得注射一个不对称的方向取法，本次没做）。
// ════════════════════════════════════════════════════════════════════════════
public class R48F6HoleFaceGateTests
{
    private readonly ITestOutputHelper _o;
    public R48F6HoleFaceGateTests(ITestOutputHelper o) { _o = o; }
    internal const string Sign = "2026-09-23，F6";

    static IEnumerable<double> Range(double a, double b, double step) { int n = (int)Math.Round((b - a) / step); for (int i = 0; i <= n; i++) yield return Math.Round(a + i * step, 6); }

    static void Save(string name, StringBuilder sb, ITestOutputHelper o)
    {
        string file = DeliverableOut.Stamped(name);
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        o.WriteLine(file);
        o.WriteLine(sb.ToString());
    }

    // ─────────────────────────────────────────────── 门 2：电极位置随格集合的跳幅（门 b 两个老红点附近每 0.001）
    /// <summary>
    /// ★ 2026-09-23（F6 审查后改名、改注释，findings #22；阈值与判据一个没动）：原名「门_电极位置不随格集合跳」与自己的证据相反 ——
    ///   面上口径在老口径跳的同一批 R 点（30.224／30.231／30.238／30.240、30.553／30.556 等）**照样有台阶**，只是跳幅小：相邻 0.001 步
    ///   +0.0384／+0.0200／+0.0341／+0.0197、+0.0379／+0.0191 %（老口径 +0.0772／+0.1213／+0.0798／+0.1838、+0.1194／+0.1794 %），
    ///   另有 30.244／30.245／30.562／30.564 的 −0.012～−0.013 %（证据 F6_门_电极位置不随格集合跳_W08_本次开跑于2026-09-23_031503.txt 与 _033018.txt，旧文件名）。
    ///   这道门证明的是「**跳幅**不超门 b 阈值」，不是「不跳」。
    /// 覆盖：在门 b 改前的两个红点 30.24、30.56（孔圆切到的格集合在这里换）附近按 0.001 取样，把门 b 的判据（相隔 0.01 的两点，
    ///   网格误差 e = (R_导航 − R_判决)/R_判决 之差 |Δe| ≤ 0.2 %，门 b 原阈值）套在**全部 10 种相位**的 0.01 格点上（每个 i 比 e[i+10] − e[i]），相邻 0.001 步的 |Δe| 只印。
    ///   改回整格钉（MeshRules.HoleFaceDirichlet = false）必须在这里红（门不空守）。
    /// 不覆盖：门 b 第三个老红点 31.00 附近没有取样；舌半宽、孔径两根扫描没有做 0.001 取样；面上口径残余台阶的来源没有在树内拆（改动者的树外探针归因于规则 A 的并格数随几何翻面，没有落证据档）；
    ///   只有 W08 片 0、舌半宽 30。
    /// 改版史（照实写）：写门时的第一版（2026-09-23 同日）判的是相邻 0.001 步 |Δe| ≤ 0.2 %，并要求改回整格钉在那把尺下红 —— 实跑改回整格钉相邻步最大只有 0.184 %，改回对照不红。
    ///   原因：老口径 30.23→30.24 那个 0.01 窗里的 +0.382 % 是 **3 次**各不到 0.2 % 的小跳（30.231／30.238／30.240：+0.121／+0.080／+0.184）加若干小步叠成的
    ///   （原注释写成「4 次小跳」，把属于 30.22→30.23 窗的 30.224 那一跳也算了进来，不对）。于是改判门 b 原判据（相隔 0.01、10 种相位全取），改回红 8 对。
    ///   原先写的换版理由「同一个 0.2 % 用在 0.001 步上等于把尺放宽十倍」不成立：台阶是跳变，跳幅不随取样步长缩放（照这个理由把 0.001 步的尺按比例收到 0.02 %，
    ///   面上口径自己的 0.0384／0.0341／0.0379 就红了）。换版的真实理由只是：让门判的量与门 b 相同（相隔 0.01）并把 10 种相位都判到。
    ///   换版没有翻转面上口径的判决（第一版面上最大 0.038 %、第二版相隔 0.01 最大 0.071 %，都过），翻转的只是改回对照（0.184 % 过 → 0.382 % 红）。
    /// </summary>
    [Fact]
    public void 门_电极位置随格集合的跳幅_不超门b阈值_切点附近10相位全判()
    {
        var p = new DesignInputs();
        var d0 = R48NMeshGateTests.Design("W08");
        double rho = Materials.PtResistivity(R48NMeshGateTests.PlateTempC) * 1e3;
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W($"F6 门：电极位置随格集合的跳幅（判「跳幅不超门 b 阈值」，不是「不跳」）　W08 片0　盘径 30.220～30.250 与 30.550～30.570 每 0.001（舌半宽 30）　导航 vs 判决等温电流场　写码 {Sign}（F6 审查后改名）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　门槛（跑前写死）：相隔 0.01 的两点 |e(R+0.01) − e(R)| ≤ {R48NMeshGateTests.RStepTolPct} %（门 b 原阈值与原间距，10 种相位全判）；相邻 0.001 步只印。改回整格钉必须红（门不空守）。");
        W("R\t口径\tR导航µΩ\tR判决µΩ\te%\t相邻0.001步Δe%\t相隔0.01的Δe%\t判读");
        int bad = 0, badOld = 0; double maxW = 0, maxWOld = 0, maxStep = 0, maxStepOld = 0; var badL = new List<string>();
        foreach (var span in new[] { Range(30.220, 30.250, 0.001).ToArray(), Range(30.550, 30.570, 0.001).ToArray() })
        {
            var eAt = new Dictionary<bool, List<double>> { [true] = new(), [false] = new() };
            foreach (double R in span)
                foreach (bool face in new[] { true, false })
                {
                    var rules = face ? null : new MeshRules { HoleFaceDirichlet = false };
                    var dn = d0.Clone(); R48NMeshGateTests.SetRW(dn, R, 30.0); var (_, _, mn, _) = R48NMeshGateTests.Build(dn, p, 0, 0, rules);
                    var dj = d0.Clone(); R48NMeshGateTests.SetRW(dj, R, 30.0); var (_, _, mj, _) = R48NMeshGateTests.Build(dj, p, 1.0, 0, rules);
                    var a = ShellCurrent.Solve(mn, R48NMeshGateTests.PlateCurrentA, rho, R48NMeshGateTests.PlateTempC);
                    var b = ShellCurrent.Solve(mj, R48NMeshGateTests.PlateCurrentA, rho, R48NMeshGateTests.PlateTempC);
                    Assert.True(a.Converged && b.Converged, $"R{R} 电流场没收敛 ⇒ 判不了");
                    Assert.Equal(face, a.HoleFaceDirichlet);
                    double Rn = rho / a.CurrentInA, Rj = rho / b.CurrentInA, e = (Rn - Rj) / Rj * 100;
                    var list = eAt[face]; list.Add(e); int i = list.Count - 1;
                    double step = i >= 1 ? e - list[i - 1] : double.NaN;
                    double win = i >= 10 ? e - list[i - 10] : double.NaN;
                    string verdict = "—";
                    if (!double.IsNaN(step)) { if (face) maxStep = Math.Max(maxStep, Math.Abs(step)); else maxStepOld = Math.Max(maxStepOld, Math.Abs(step)); }
                    if (!double.IsNaN(win))
                    {
                        bool ok = Math.Abs(win) <= R48NMeshGateTests.RStepTolPct;
                        verdict = ok ? "过" : "**不过**";
                        if (face) { maxW = Math.Max(maxW, Math.Abs(win)); if (!ok) { bad++; badL.Add($"R{R - 0.01:0.000}→{R:0.000} Δe {win:+0.000;-0.000}"); } }
                        else { maxWOld = Math.Max(maxWOld, Math.Abs(win)); if (!ok) badOld++; }
                    }
                    string F4(double v) => double.IsNaN(v) ? "—" : (Math.Abs(v) < 5e-5 ? 0.0 : v).ToString("+0.0000;-0.0000");
                    W($"{R:0.000}\t{(face ? "面上" : "改回整格钉")}\t{Rn * 1e6:0.0000}\t{Rj * 1e6:0.0000}\t{e:+0.0000;-0.0000}\t{F4(step)}\t{F4(win)}\t{verdict}");
                }
        }
        W($"── 面上口径：相隔 0.01 不过 {bad} 对，最大 {maxW:0.0000} %；相邻 0.001 步最大 {maxStep:0.0000} %");
        W($"── 改回整格钉：相隔 0.01 不过 {badOld} 对，最大 {maxWOld:0.0000} %；相邻 0.001 步最大 {maxStepOld:0.0000} %");
        Save("F6_门_电极位置随格集合的跳幅_W08.txt", sb, _o);
        Assert.True(bad == 0, $"面上口径相隔 0.01 的跳幅超门 b 阈值 {R48NMeshGateTests.RStepTolPct} %：{string.Join("　", badL)}");
        Assert.True(badOld > 0, "改回整格钉在这两个区间里没红 —— 门空守");
    }

    // ─────────────────────────────────────────────── 门 3：电流守恒（面上口径）
    /// <remarks>2026-09-23（F6 审查后，findings #2）：加 W08 判决网格与误差预算设计 h = 0.5 两张（生产 tol 下 Vmin 为负的那一类网格），不许只在选定网格上成立。</remarks>
    static IEnumerable<(string name, ShellMesh m)> ConservationMeshes()
    {
        var p = new DesignInputs();
        var d = R48NMeshGateTests.Design("W08");
        var (_, _, mW, _) = R48NMeshGateTests.Build(d, p, 0);
        yield return ("W08 导航网格（默认几何）", mW);
        var (_, _, mWj, _) = R48NMeshGateTests.Build(R48NMeshGateTests.Design("W08"), p, 1.0);
        yield return ("W08 判决网格（默认几何，fine 1）", mWj);
        yield return ("误差预算设计片0 h = 0.5", BudgetMesh(0.5, null).m);
        var hole = R48NSliverGateTests.Shapes().First(s => s.name.Contains("舌孔")).g;
        yield return ("Heater1 舌孔R30（h = 2）", FlangeMesher.Build(hole, 0, 2.0, 8.0, 50.0, p.BusbarClampLengthMm));
        yield return ("双舌板（h = 2）", FlangeMesher.Build(TwoTab(), 0, 2.0, 2.0, 1e6, 40.0, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true));
    }

    /// <summary>「J 积分」一项的改回对照网格：W08 导航／判决（默认几何），MeshRules.HoleFaceDirichlet = false（老口径整格钉），网格其余规则照生产。</summary>
    static IEnumerable<(string name, ShellMesh m)> ConservationMeshesOld()
    {
        var p = new DesignInputs();
        var off = new MeshRules { HoleFaceDirichlet = false };
        var (_, _, mW, _) = R48NMeshGateTests.Build(R48NMeshGateTests.Design("W08"), p, 0, 0, off);
        yield return ("W08 导航网格（默认几何）【改回整格钉】", mW);
        var (_, _, mWj, _) = R48NMeshGateTests.Build(R48NMeshGateTests.Design("W08"), p, 1.0, 0, off);
        yield return ("W08 判决网格（默认几何，fine 1）【改回整格钉】", mWj);
    }

    internal static FlangePlate TwoTab() => new()
    {
        DiscRadiusMm = 60, HoleRadiusMm = 26, TabEndXMm = -199.5, TabEndHalfWidthMm = 40,
        ThicknessMm = 2.0, ThickenedMm = 2.0, TabThicknessMm = 2.0, TabParallel = true, WeldFilletLegMm = 0, TwoTabs = true,
    };

    /// <summary>
    /// 重构出的 J 在孔面上的积分 Σ (J_A·n̂)·弦长·t_A（A）。n̂ = 弧中点指向孔心 −Mid/|Mid|、弦长 = 2r·sin(L/2r) —— 对格内均匀的 J，∫_弧 J·n ds = J·∫n ds = J·n̂·弦长，
    /// 这是几何恒等式；直边孔面 n̂ = 形心指向边中点、弦长 = 面长。落在压接格集合上的孔面（J = 0 的格）不计，条数另报。
    /// </summary>
    static (double outJ, int skipped) HoleFaceJIntegral(ShellMesh m, ShellCurrentResult r)
    {
        var clamp = m.ClampSetCells();
        double s = 0; int skip = 0;
        foreach (var f in m.Faces)
        {
            if (f.B >= 0 || f.Tag != ShellMesh.TagHole) continue;
            if (clamp[f.A]) { skip++; continue; }
            double nx, nz, chord;
            if (!double.IsNaN(f.ArcRadiusMm))
            {
                double rm = Math.Sqrt(f.Mid.X * f.Mid.X + f.Mid.Z * f.Mid.Z);
                nx = -f.Mid.X / rm; nz = -f.Mid.Z / rm;
                chord = 2 * f.ArcRadiusMm * Math.Sin(f.Length / (2 * f.ArcRadiusMm));
            }
            else
            {
                var dd = f.Mid - m.Centroid[f.A]; double ll = dd.Norm;
                nx = dd.X / ll; nz = dd.Z / ll; chord = f.Length;
            }
            s += (r.JxAPerMm2[f.A] * nx + r.JzAPerMm2[f.A] * nz) * chord * m.Thickness[f.A];
        }
        return (s, skip);
    }

    /// <summary>
    /// 覆盖（F6 审查后照实改写，findings #1／#2／#25／#68）：
    ///   ① 装配一致（**只证装配一致，不证物理**）：恒等式 Σ_自由格 r_i = I_in − I_out 到舍入 1e-12·I_in；ConservationError ≤ (Σ|r_i| + 1e-12·I_in)/I_in；
    ///      测试侧逐面复算 Σ_孔面 t·L·V/DistAB（σ = 1，等温）= CurrentOutA 到 1e-12。恒等式对任意 V 都成立（内部面成对抵消、短路面 in／out 各计一次），
    ///      第二条是第一条的三角不等式推论，第三条与 ShellCurrent 是同一个算式 —— 三条只能抓 in、out、残差三处簿记互相漂开（例如 outSum 漏掉或筛错了孔面），
    ///      抓不到 gB 大小或 d 的取法错（那归 F6b 门与 J 峰门）。
    ///   ② **独立一项**（2026-09-23 F6 审查后加）：重构出的 J 在孔面上积分 Σ (J·n̂)·弦长·t（<see cref="HoleFaceJIntegral"/>）与流出电流 I·CurrentOutA/CurrentInA
    ///      （面导度 Σ g·V 定标到安培）相对差 ≤ 5 %。两者算法不同：一边是最小二乘重构的格 J（生产判「全体 J &lt; 11」用的就是它），一边是孔面导度 —— 差别就是孔边 J 重构的误差。
    ///      5 % 借用 R48NSliverGateTests.ScaleSpreadTol（同为 J 重构离散误差的容许量），跑前定；不是推导。
    ///      改回对照：改回整格钉（W08 导航／判决）时孔格是电极格、J 重构里没有孔面通量 ⇒ 这一项必须 &gt; 5 %（门不空守）。
    ///      首跑实测（2026-09-23 05:53，Linux 镜像，F6_门_电流守恒_面上_本次开跑于2026-09-23_055337.txt）：面上口径 0.16～1.27 %；改回 8.24 %（导航）／5.86 %（判决）——
    ///      判决网格的改回对照只超阈值 0.86 个百分点，余量薄，照实写。
    ///   ③ 离散极值原理：所有面导度 ≥ 0、gB &gt; 0 ⇒ 矩阵是 M 矩阵，**精确**离散解 0 ≤ V ≤ 1（这由结构保证，不检验 gB 的大小：gB 乘 2 照样满足）。
    ///      CG 迭代解 V = V* − A⁻¹r，停机判据 ‖r‖∞ ≤ tol·‖b‖∞；‖A⁻¹‖∞ 随网格而变、门内拿不到（ShellCurrent 不交出矩阵），推不出生产 tol 下的严格界 ⇒
    ///      生产 tol = 1e-9 下的 Vmin 只印（判决网格实测为负、1e-10 量级，findings #2），另在 tol = 1e-13 重解后判 V ∈ [−1e-12, 1 + 1e-12]
    ///      （1e-13 沿用 门_双舌板孔边对称；1e-12 选定：舍入余量）。‖V(1e-9) − V(1e-13)‖∞ 照印 —— 那就是生产 tol 那一档 CG 的实测误差。
    ///   网格：W08 导航、W08 判决（默认几何）、误差预算设计片0 h = 0.5、Heater1 舌孔（锥形舌 + 舌上开孔）、双舌板（两端压接）。
    /// 不覆盖：孔边弧段内的通量分布（一格一个未知量，分辨不了）；gB 大小与 d 取法的物理对错（F6b 门、J 峰门）；温度相关 σ（本门等温）；图纸（栅格）路径；
    ///   ② 的 5 % 只卡「量级对」，J 重构在孔边的逐格精度不在这里判。
    /// </summary>
    [Fact]
    public void 门_电流守恒_面上()
    {
        const double I = 1000;
        double jIntTol = R48NSliverGateTests.ScaleSpreadTol;
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W($"F6 门：电流守恒（孔面上定电位）　写码 {Sign}（F6 审查后改写：加 J 积分独立项、判决网格与 h0.5、V 界改在 tol 1e-13 重解上判）　开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W("门槛（跑前写死）：①（只证装配一致，不证物理）|(I_in − I_out) − Σr_i| ≤ 1e-12·I_in；Cons ≤ (Σ|r_i| + 1e-12·I_in)/I_in；复算 Σ t·L·V/d 与 CurrentOutA 相对差 ≤ 1e-12（1e-12 选定的舍入余量）；");
        W($"　　　　　　　　　　　② 孔面 J 积分 Σ(J·n̂)·弦长·t 与 I·CurrentOutA/CurrentInA 相对差 ≤ {jIntTol:P0}（借用 R48NSliverGateTests.ScaleSpreadTol）；改回整格钉（W08 导航／判决）必须 > {jIntTol:P0}；");
        W("　　　　　　　　　　　③ tol 1e-13 重解上 V ∈ [−1e-12, 1 + 1e-12]；生产 tol 1e-9 下的 Vmin 只印。");
        W("网格\t单元\t孔面\t孔面落在电极上\tI_in\tI_out\t守恒误差\tΣr_i\tΣ|r_i|/I_in\t恒等式偏差/I_in\t复算出流相对差\tJ积分A\t流出A\tJ积分相对差\t跳过(压接格)孔面\tVmin(tol1e-9)\tVmin(tol1e-13)\tVmax(tol1e-13)\t‖V(1e-9)−V(1e-13)‖∞\tCG轮\t收敛\t判读");
        int bad = 0; var badL = new List<string>();
        foreach (var (name, m) in ConservationMeshes())
        {
            var r = ShellCurrent.Solve(m, I, 1.1e-4, 1300);
            var rt = ShellCurrent.Solve(m, I, 1.1e-4, 1300, null, 200000, 1e-13);
            Assert.True(r.Converged && rt.Converged, name + " 电流场没收敛 ⇒ 判不了");
            Assert.True(r.HoleFaceDirichlet && r.HoleFaceCount > 0, name + "：孔面上定电位没生效");
            double idErr = Math.Abs((r.CurrentInA - r.CurrentOutA) - r.ResidualSum) / r.CurrentInA;
            double vmin = r.V.Min(), vminT = rt.V.Min(), vmaxT = rt.V.Max();
            double dV = 0; for (int i = 0; i < m.CellCount; i++) dV = Math.Max(dV, Math.Abs(r.V[i] - rt.V[i]));
            double outRe = 0;
            foreach (var f in m.Faces)
                if (f.B < 0 && f.Tag == ShellMesh.TagHole) outRe += m.Thickness[f.A] * f.Length / f.DistAB * r.V[f.A];
            double outRel = Math.Abs(outRe - r.CurrentOutA) / r.CurrentOutA;
            var (outJ, skipped) = HoleFaceJIntegral(m, r);
            double outA = I * r.CurrentOutA / r.CurrentInA;
            double jRel = Math.Abs(outJ - outA) / outA;
            var notes = new List<string>();
            if (!(idErr <= 1e-12)) notes.Add($"恒等式偏差 {idErr:E2}");
            if (!(r.ConservationError <= (r.ResidualAbsSum + 1e-12 * r.CurrentInA) / r.CurrentInA)) notes.Add($"守恒 {r.ConservationError:E2} > Σ|r|/I_in {r.ResidualAbsSum / r.CurrentInA:E2}");
            if (!(outRel <= 1e-12)) notes.Add($"复算出流差 {outRel:E2}");
            if (r.HoleFacesOnElectrode > 0 || skipped > 0) notes.Add($"孔面落在电极／压接格上 {r.HoleFacesOnElectrode}／{skipped} ⇒ J 积分判不了");
            else if (!(jRel <= jIntTol)) notes.Add($"J 积分相对差 {jRel:P2} > {jIntTol:P0}");
            if (!(vminT >= -1e-12 && vmaxT <= 1 + 1e-12)) notes.Add($"V 出界（tol 1e-13）[{vminT:R}, {vmaxT:R}]");
            W($"{name}\t{m.CellCount}\t{r.HoleFaceCount}\t{r.HoleFacesOnElectrode}\t{r.CurrentInA:R}\t{r.CurrentOutA:R}\t{r.ConservationError:E2}\t{r.ResidualSum:E2}\t{r.ResidualAbsSum / r.CurrentInA:E2}\t{idErr:E2}\t{outRel:E2}\t{outJ:0.0000}\t{outA:0.0000}\t{jRel:P3}\t{skipped}\t{vmin:E3}\t{vminT:E3}\t{vmaxT:0.000000000000}\t{dV:E2}\t{r.Iterations}/{rt.Iterations}\t{r.Converged}\t{(notes.Count == 0 ? "过" : "**" + string.Join("；", notes) + "**")}");
            if (notes.Count > 0) { bad++; badL.Add(name + "：" + string.Join("；", notes)); }
        }
        int oldRed = 0, oldN = 0;
        foreach (var (name, m) in ConservationMeshesOld())
        {
            var r = ShellCurrent.Solve(m, I, 1.1e-4, 1300);
            Assert.True(r.Converged, name + " 电流场没收敛 ⇒ 判不了");
            Assert.False(r.HoleFaceDirichlet, name + "：改回整格钉没生效");
            var (outJ, skipped) = HoleFaceJIntegral(m, r);
            double outA = I * r.CurrentOutA / r.CurrentInA;
            double jRel = Math.Abs(outJ - outA) / outA;
            oldN++; if (jRel > jIntTol) oldRed++;
            W($"{name}\t{m.CellCount}\t{r.HoleFaceCount}\t—\t{r.CurrentInA:R}\t{r.CurrentOutA:R}\t{r.ConservationError:E2}\t—\t—\t—\t—\t{outJ:0.0000}\t{outA:0.0000}\t{jRel:P3}\t{skipped}\t{r.V.Min():E3}\t—\t—\t—\t{r.Iterations}\t{r.Converged}\t{(jRel > jIntTol ? "改回红（门不空守）" : "**改回没红 ⇒ J 积分一项空守**")}");
        }
        W($"── 面上口径不过 {bad}　改回整格钉 J 积分红 {oldRed}/{oldN}");
        Save("F6_门_电流守恒_面上.txt", sb, _o);
        Assert.True(bad == 0, string.Join("　", badL));
        Assert.True(oldRed == oldN, $"改回整格钉 J 积分只红 {oldRed}/{oldN} —— J 积分一项空守");
    }

    // ─────────────────────────────────────────────── 门 4：弧面距离的单点通量误差界（F6b）
    /// <summary>
    /// 覆盖：在生产网格的每条弧面上代入精确径向解 V = ln(|C|/rh)（σ = 1）：
    ///   两点通量 t·L·V_C/d ÷ 精确通量 t·L/rh = ln(1 + x)/x（d = |C| − rh 时，x = d/rh）∈ [1 − x/2, 1] —— 由 x − x²/2 ≤ ln(1+x) ≤ x（x ≥ 0）推出，容差只留 1e-12（选定舍入余量）。
    ///   ⚠（F6 审查后照实写，findings #3／#13／#34）：生产口径下 DistAB 就是 |C| − rh，比值**恒等于** ln(1+x)/x，法向距那一支按构造必过 ——
    ///   它实际核的只是「DistAB 等于到孔圆的法向距」（与配方 HoleArcNormalDist 逐面比对是同一件事），并在纯径向场下给出单点通量误差界。
    ///   有牙的是反向那一支：改回（MeshRules.HoleArcNormalDist = false，距离 = 形心到弧中点的直线距离）必须有面出界（30.24 那条薄片面比值 ≈ 0.04，树外探针实测）。
    ///   前提（照实断言，不是阈值）：解析路径上形心都在孔圆外、没有触到距离下限（HoleArcCentroidInside = HoleArcDistAtFloor = 0）；配方开关位与量出来的位都为真／都为假。
    ///   2026-09-23（F6 审查后加，findings #13）：另用一张手造两格小网格覆盖 <see cref="ShellMesh.BoundaryDistMm"/> 的 abs 支（形心落在孔圆内侧 0.2 mm ⇒ DistAB = 0.2）
    ///   与下限支（形心离孔圆 1e-9 mm ⇒ DistAB = GeomTolMm）以及两个诊断计数 —— 生产解析路径上这两支都不出现（只在图纸路径出现），原先没有任何门跑到过。
    /// 不覆盖：非径向场（等电位孔边上梯度沿法向，但沿弧的通量分布一格一个未知量分辨不了）；一格多段弧、形心径向垂足落在别段弧上的面（按同一个法向距给导度，没有独立参照）；
    ///   abs 支与下限支只核「算式走对了哪一支」，不核那两种情形下这个距离在物理上是否合适；图纸（栅格）路径的真实网格（孔弧缺口、形心在孔内的面）不在算例里；
    ///   热场 gHole 读同一个 DistAB，其通量正确性也只在同样的意义下被覆盖。
    /// </summary>
    [Fact]
    public void 门_弧面距离_单点通量误差界()
    {
        var p = new DesignInputs();
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W($"F6b 门：弧面距离的单点通量误差界　写码 {Sign}　开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W("门槛（跑前写死）：每条弧面 ln(1+x)/x·（x·rh/d）∈ [1 − x/2 − 1e-12, 1 + 1e-12]（x = (|C|−rh)/rh）；改回直线距离必须有面出界。");
        W("设计\tR\t档\t口径\t弧面\t形心在孔内\t触下限\t比值最小\t该面 x\t该面下界\t出界面数");
        int bad = 0, badOld = 0; double minOld = double.PositiveInfinity; var badL = new List<string>();
        foreach (var (which, R) in new[] { ("W08", 30.24), ("W08", 30.56), ("W08", 31.00), ("W06", double.NaN) })
            foreach (double fine in new[] { 0.0, 1.0 })
                foreach (bool normal in new[] { true, false })
                {
                    var d = R48NMeshGateTests.Design(which);
                    if (!double.IsNaN(R)) R48NMeshGateTests.SetRW(d, R, 30.0);
                    var rules = normal ? null : new MeshRules { HoleArcNormalDist = false };
                    var (_, g, m, _) = R48NMeshGateTests.Build(d, p, fine, 0, rules);
                    double rh = m.HoleRadiusMm;
                    int arcs = 0, outN = 0; double rMin = double.PositiveInfinity, xAt = double.NaN, loAt = double.NaN;
                    foreach (var f in m.Faces)
                    {
                        if (f.B >= 0 || double.IsNaN(f.ArcRadiusMm)) continue;
                        arcs++;
                        var c = m.Centroid[f.A];
                        double rc = Math.Sqrt(c.X * c.X + c.Z * c.Z);
                        double x = (rc - rh) / rh;
                        double vC = Math.Log(rc / rh);
                        double ratio = vC * rh / f.DistAB;   // (t·L·V_C/d) ÷ (t·L/rh)
                        double lo = 1 - x / 2;
                        if (!(ratio >= lo - 1e-12 && ratio <= 1 + 1e-12)) outN++;
                        if (ratio < rMin) { rMin = ratio; xAt = x; loAt = lo; }
                    }
                    W($"{which}\t{(double.IsNaN(R) ? "默认" : R.ToString("0.00"))}\t{(fine == 0 ? "导航" : "判决")}\t{(normal ? "法向距" : "改回直线距")}\t{arcs}\t{m.HoleArcCentroidInside}\t{m.HoleArcDistAtFloor}\t{rMin:0.000000}\t{xAt:E3}\t{loAt:0.000000}\t{outN}");
                    Assert.True(arcs > 0, "没有弧面 ⇒ 门判不了");
                    if (normal)
                    {
                        Assert.Equal(0, m.HoleArcCentroidInside);
                        Assert.Equal(0, m.HoleArcDistAtFloor);
                        Assert.True(m.Recipe!.HoleArcNormalDist);
                        Assert.True(m.Recipe!.HoleArcNormalDistSwitch);
                        if (outN > 0) { bad++; badL.Add($"{which} R{R} fine{fine} 出界 {outN}"); }
                    }
                    else { badOld += outN; minOld = Math.Min(minOld, rMin); Assert.False(m.Recipe!.HoleArcNormalDist); Assert.False(m.Recipe!.HoleArcNormalDistSwitch); }
                }
        W($"── 法向距：出界 {bad} 组　改回直线距：出界面 {badOld} 条，比值最小 {minOld:0.0000}");

        var handBad = new List<string>();
        // 2026-09-23（F6 审查后加，findings #13）：手造两格小网格 —— 覆盖 BoundaryDistMm 的 abs 支与下限支（生产解析路径上都不出现）
        //   孔半径 10 mm；格 0 矩形 [9.5, 10.5]×[−0.5, 0.5]、形心手放 (9.8, 0, 0)（孔圆内侧 0.2 mm）；格 1 矩形 [−0.5, 0.5]×[9.5, 10.5]、形心手放 (0, 0, 10 + 1e-9)（离孔圆 1e-9 &lt; GeomTolMm）。
        //   弧面由生产的 FlangeMesher.AddHoleArcFaces 建（按节点矩形），距离由它调 BoundaryDistMm 填；诊断由 ComputeHoleTagDiagnostics 数。
        {
            const double rh0 = 10.0;
            var hm = new ShellMesh();
            void Cell(double x0, double x1, double z0, double z1, Vec3 c)
            {
                int k = hm.Nodes.Count;
                hm.Nodes.Add(new Vec3(x0, 0, z0)); hm.Nodes.Add(new Vec3(x1, 0, z0)); hm.Nodes.Add(new Vec3(x1, 0, z1)); hm.Nodes.Add(new Vec3(x0, 0, z1));
                hm.Cells.Add(new[] { k, k + 1, k + 2, k + 3 });
                hm.Area.Add((x1 - x0) * (z1 - z0)); hm.Centroid.Add(c); hm.Thickness.Add(1.0); hm.Part.Add(0); hm.Frac.Add(1.0);
            }
            Cell(9.5, 10.5, -0.5, 0.5, new Vec3(9.8, 0, 0));
            Cell(-0.5, 0.5, 9.5, 10.5, new Vec3(0, 0, rh0 + 1e-9));
            FlangeMesher.AddHoleArcFaces(hm, rh0, _ => ShellMesh.TagHole);
            hm.ComputeHoleTagDiagnostics(rh0);
            var f0 = hm.Faces.Where(f => f.A == 0 && f.B < 0).ToArray();
            var f1 = hm.Faces.Where(f => f.A == 1 && f.B < 0).ToArray();
            W("手造两格小网格（孔半径 10）：格\t形心|C|\t弧面数\tDistAB\t期望\t走的支");
            foreach (var f in f0) W($"0\t{Math.Sqrt(9.8 * 9.8):R}\t{f0.Length}\t{f.DistAB:R}\t{rh0 - 9.8:R}\tabs（形心在孔内）");
            foreach (var f in f1) W($"1\t{rh0 + 1e-9:R}\t{f1.Length}\t{f.DistAB:R}\t{ShellMesh.GeomTolMm:R}\t下限（|C| − rh = 1e-9 < GeomTolMm）");
            W($"诊断：形心在孔内 {hm.HoleArcCentroidInside}（应 1）　触下限 {hm.HoleArcDistAtFloor}（应 1）");
            if (!(f0.Length == 1 && f1.Length == 1)) handBad.Add($"手造小网格弧面数 {f0.Length}／{f1.Length}，应各 1");
            else
            {
                if (!(Math.Abs(f0[0].DistAB - (rh0 - 9.8)) <= 1e-12)) handBad.Add($"abs 支：DistAB {f0[0].DistAB:R}，应为 {rh0 - 9.8:R}");
                if (f1[0].DistAB != ShellMesh.GeomTolMm) handBad.Add($"下限支：DistAB {f1[0].DistAB:R}，应为 GeomTolMm {ShellMesh.GeomTolMm:R}");
            }
            if (hm.HoleArcCentroidInside != 1) handBad.Add($"形心在孔内计数 {hm.HoleArcCentroidInside}，应 1");
            if (hm.HoleArcDistAtFloor != 1) handBad.Add($"触下限计数 {hm.HoleArcDistAtFloor}，应 1");
            W($"── 手造小网格：{(handBad.Count == 0 ? "abs 支、下限支、两个计数都对" : "**" + string.Join("；", handBad) + "**")}");
        }
        Save("F6b_门_弧面距离_单点通量误差界.txt", sb, _o);
        Assert.True(handBad.Count == 0, string.Join("；", handBad));
        Assert.True(bad == 0, string.Join("　", badL));
        Assert.True(badOld > 0, "改回直线距离没有面出界 —— 门空守");
    }

    // ─────────────────────────────────────────────── 门 5：孔边 J 峰随加密收敛
    /// <summary>误差预算设计（R48ClampRecipeTests.Design 同一份输入，逐字搬来；那边是 private）。</summary>
    internal static (DesignSpec d, DesignInputs p) BudgetDesign()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.FlangeInsulated = true;
        d.FlangeInsulMm = 20.0;
        d = d.Fit();
        d.SizeTongues(p);
        return (d, p);
    }

    /// <summary>误差预算设计片 0 在整片加密 h 下的网格（h ≥ 2 = 导航档原样）；配方 = FlangeMesher.BuildWith（与 Build 同一份），规则可注射。</summary>
    internal static (LineCase lc, FlangePlate g, ShellMesh m) BudgetMesh(double h, MeshRules? rules)
    {
        var (d, p) = BudgetDesign();
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        var lc = d.BuildCase(p, checkRamp: false);
        if (h < 2) MeshAdapt.RefineWholeMesh(lc, h, radius);
        var g = d.Plate(0, d.DiscFloorMm(p)); g.HoleRadiusMm = lc.TubeIdMm * 0.5 + lc.WallMm;
        var m = FlangeMesher.BuildWith(g, rules, null, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
        return (lc, g, m);
    }

    /// <summary>
    /// 覆盖：生产判据用的 JMax（LineRunner 直接取 JMaxAPerMm2 判「全体 J &lt; 11」）在孔边随加密收敛。误差预算设计片 0、等温（控温点）、1213 A，h = 1／0.5／0.25：
    ///   三档 JMax 极差 ≤ R48NSliverGateTests.ScaleSpreadTol（5 %，同一把尺，直接引用常量）。改回整格钉：孔格 J 取不到真值、越细越大（15.280 → 16.152，+5.71 %）⇒ 必须红。
    ///   F6 审查后补（findings #26）：阈值原为手抄字面量 0.05，现直接引用 R48NSliverGateTests.ScaleSpreadTol（改成 internal）；每一档断言结果的 HoleFaceDirichlet 与所注射的口径一致（开关真生效）。
    ///   ⚠ 改回对照的余量很薄：改回极差 5.71 %，只超阈值 0.71 个百分点（证据 F6a_门_孔边J峰随加密收敛_本次开跑于2026-09-23_045609.txt）。
    /// 不覆盖：导航档 h = 2（生产导航网格）与 W06；图纸路径；温度相关 σ（本门等温）；峰落在孔边以外时（本算例三档峰都在孔边附近，照印峰格位置）。
    /// </summary>
    [Fact]
    public void 门_孔边J峰随加密收敛()
    {
        double spreadTol = R48NSliverGateTests.ScaleSpreadTol;   // 沿用（F6 审查后改为直接引用，不再手抄）
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W($"F6a 门：孔边 J 峰随加密收敛　误差预算设计片0　等温 1213 A　写码 {Sign}　开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"门槛（跑前写死）：h = 1／0.5／0.25 三档 JMax 极差 ≤ {spreadTol:P0}；改回整格钉必须 > {spreadTol:P0}。");
        W("口径\th\t单元\tJMax\t峰格半径−rh\t峰格是孔格");
        var spread = new Dictionary<bool, double>();
        foreach (bool face in new[] { true, false })
        {
            var peaks = new List<double>();
            foreach (double h in new[] { 1.0, 0.5, 0.25 })
            {
                var (lc, g, m) = BudgetMesh(h, face ? null : new MeshRules { HoleFaceDirichlet = false });
                double tSet = lc.SetpointC[0];
                var sc = ShellCurrent.SolveFor(lc, m, 1213, Materials.PtResistivity(tSet) * 1e3, tSet);
                Assert.True(sc.Converged, "电流场没收敛 ⇒ 判不了");
                Assert.Equal(face, sc.HoleFaceDirichlet);   // F6 审查后补：注射的口径真生效了
                var c = m.Centroid[sc.JMaxCell];
                bool holeCell = m.Faces.Any(f => f.B < 0 && f.A == sc.JMaxCell && f.Tag == ShellMesh.TagHole);
                W($"{(face ? "面上" : "改回整格钉")}\t{h}\t{m.CellCount}\t{sc.JMaxAPerMm2:0.0000}\t{Math.Sqrt(c.X * c.X + c.Z * c.Z) - m.HoleRadiusMm:0.000}\t{holeCell}");
                peaks.Add(sc.JMaxAPerMm2);
            }
            spread[face] = (peaks.Max() - peaks.Min()) / peaks.Min();
            W($"{(face ? "面上" : "改回整格钉")}：三档极差 {spread[face]:P2}");
        }
        W($"改回对照余量：改回极差 − 阈值 = {(spread[false] - spreadTol) * 100:+0.00;-0.00} 个百分点；面上口径 阈值 − 极差 = {(spreadTol - spread[true]) * 100:+0.00;-0.00} 个百分点");
        Save("F6a_门_孔边J峰随加密收敛.txt", sb, _o);
        Assert.True(spread[true] <= spreadTol, $"面上口径 J 峰三档极差 {spread[true]:P2} > {spreadTol:P0}");
        Assert.True(spread[false] > spreadTol, $"改回整格钉 J 峰三档极差 {spread[false]:P2} 没超 {spreadTol:P0} —— 门空守");
    }

    // ─────────────────────────────────────────────── 门 6：孔面只在孔圆上（F6c）
    /// <summary>
    /// 覆盖：F6c —— 生产网格上所有管孔面都是孔圆上的弧面（有限的 ArcRadiusMm、中点离孔圆 ≤ 1e-9 mm），3 mm 判定带不再把孔附近的直边真边界定成管孔。
    /// 算例：W08 盘径 28／舌半宽 26（环宽 2.2 mm &lt; 3 mm，切点旁舌直边落进判定带）导航与判决档，另加 W08／W06 默认几何。
    /// 改回（MeshRules.HoleTagArcOnly = false）在 R28／w26 上必须有非弧孔面（门不空守；树外探针判决档 2 条共 2.0 mm），只断言 &gt; 0、数只印。
    /// 1e-9 mm 选定（舍入余量，不是推导）。
    /// F6 审查后补（findings #16）：配方的开关位 HoleTagArcOnlySwitch 与量出来的 HoleTagArcOnly 分开印、分开断言 —— 量出来的那一位在 R28／w26 导航档关掉 F6c 时仍为真
    ///   （几何上带里没有直边），判不出规则开没开；开关位才判得出。
    /// 不覆盖：只有 3 个几何（W08 R28／w26、W08 默认、W06 默认）；图纸（栅格）路径（F6c 在那里摘掉的是舌直边与盘缘栅格台阶，没有门守）；阶梯孔边路径（HoleArcFaces 关时 F6c 不起作用）。
    /// </summary>
    [Fact]
    public void 门_孔面只在孔圆上()
    {
        var p = new DesignInputs();
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W($"F6c 门：孔面只在孔圆上　写码 {Sign}　开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W("门槛（跑前写死）：生产网格上每条管孔面 ArcRadiusMm 有限、|r_mid − rh| ≤ 1e-9；配方 HoleTagArcOnly 为真。改回（HoleTagArcOnly = false）在 R28／w26 上非弧孔面 > 0。");
        W("算例\t档\t口径\t孔面\t非弧孔面\t非弧孔面总长mm\t离圆最大mm\t配方开关位HoleTagArcOnlySwitch\t配方量得HoleTagArcOnly");
        int bad = 0, nonArcOld = 0; var badL = new List<string>();
        foreach (var (name, which, R, w) in new[] { ("W08 R28 w26", "W08", 28.0, 26.0), ("W08 默认", "W08", double.NaN, double.NaN), ("W06 默认", "W06", double.NaN, double.NaN) })
            foreach (double fine in new[] { 0.0, 1.0 })
                foreach (bool arcOnly in new[] { true, false })
                {
                    if (!arcOnly && double.IsNaN(R)) continue;
                    var d = R48NMeshGateTests.Design(which);
                    if (!double.IsNaN(R)) R48NMeshGateTests.SetRW(d, R, w);
                    var (_, _, m, _) = R48NMeshGateTests.Build(d, p, fine, 0, arcOnly ? null : new MeshRules { HoleTagArcOnly = false });
                    double rh = m.HoleRadiusMm; int nh = 0, nNon = 0; double lNon = 0, off = 0;
                    foreach (var f in m.Faces)
                    {
                        if (f.B >= 0 || f.Tag != ShellMesh.TagHole) continue;
                        nh++;
                        double rm = Math.Sqrt(f.Mid.X * f.Mid.X + f.Mid.Z * f.Mid.Z);
                        if (double.IsNaN(f.ArcRadiusMm)) { nNon++; lNon += f.Length; }
                        else off = Math.Max(off, Math.Abs(rm - rh));
                    }
                    W($"{name}\t{(fine == 0 ? "导航" : "判决")}\t{(arcOnly ? "只认弧面" : "改回按带")}\t{nh}\t{nNon}\t{lNon:0.000}\t{off:E2}\t{m.Recipe!.HoleTagArcOnlySwitch}\t{m.Recipe!.HoleTagArcOnly}");
                    Assert.True(nh > 0, name + " 没有孔面");
                    Assert.Equal(arcOnly, m.Recipe!.HoleTagArcOnlySwitch);   // F6 审查后补：配方记的开关位 = 注射的规则
                    if (arcOnly)
                    {
                        if (nNon > 0 || off > 1e-9 || !m.Recipe!.HoleTagArcOnly) { bad++; badL.Add($"{name} fine{fine}: 非弧 {nNon}、离圆 {off:E2}"); }
                    }
                    else nonArcOld += nNon;
                }
        W($"── 只认弧面：不过 {bad}　改回按带：非弧孔面共 {nonArcOld} 条");
        Save("F6c_门_孔面只在孔圆上.txt", sb, _o);
        Assert.True(bad == 0, string.Join("　", badL));
        Assert.True(nonArcOld > 0, "改回按带在 R28／w26 上没有非弧孔面 —— 门空守");
    }

    // ─────────────────────────────────────────────── 门 7：双舌板孔边对称
    /// <summary>
    /// 覆盖：面上口径在左右对称的双舌板上给出左右对称的场（孔面导度、J 重构的孔面项都不带方向偏置）。平行边双舌、压接 40 mm、整面 + 面上、不铺细带，均匀 h = 2 与 1.3。
    /// V 镜像差、J 镜像差、左右两半孔面出流相对差 ≤ 1e-9（借用条带门的 1e-9：那边是相对误差，这里套在 V（0…1）、J（A/mm²）与相对出流差上 —— 借用、选定，不是推导）。
    /// 前置：网格镜像表完整（缺一格就判不了、门红）。线性解容差收到 1e-13（与树外探针同）：门量的是离散格式的对称性，不是 CG 停在哪里。
    /// **无改回对照**（F6 审查后照实写，findings #21／#45／#66）：改回整格钉在对称几何上同样给出对称的场，没有开关能让它红；要证门能红得注射一个不对称的方向取法，本次没做。
    /// 不覆盖：只能查出**镜像不对称**的偏置（对称的错误 —— 例如 gB 整体偏大 —— 它看不见）；只有平行边双舌、两档均匀网格；非对称几何。
    /// </summary>
    [Fact]
    public void 门_双舌板孔边对称()
    {
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W($"F6a 门：双舌板孔边对称　写码 {Sign}　开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W("门槛（跑前写死）：镜像表完整；V、J 镜像最大差与左右孔面出流相对差 ≤ 1e-9。");
        int bad = 0; var badL = new List<string>();
        foreach (double h in new[] { 2.0, 1.3 })
        {
            var m = FlangeMesher.Build(TwoTab(), 0, h, h, 1e6, 40.0, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
            var key = new Dictionary<(long, long), int>();
            for (int i = 0; i < m.CellCount; i++) key[((long)Math.Round(m.Centroid[i].X * 1e6), (long)Math.Round(m.Centroid[i].Z * 1e6))] = i;
            int miss = 0; var mir = new int[m.CellCount];
            for (int i = 0; i < m.CellCount; i++)
                if (!key.TryGetValue(((long)Math.Round(-m.Centroid[i].X * 1e6), (long)Math.Round(m.Centroid[i].Z * 1e6)), out mir[i])) { miss++; mir[i] = -1; }
            var r = ShellCurrent.Solve(m, 1000, 1.1e-4, 1300, null, 200000, 1e-13);
            double vErr = 0, jErr = 0;
            for (int i = 0; i < m.CellCount; i++)
                if (mir[i] >= 0) { vErr = Math.Max(vErr, Math.Abs(r.V[i] - r.V[mir[i]])); jErr = Math.Max(jErr, Math.Abs(r.JMagAPerMm2[i] - r.JMagAPerMm2[mir[i]])); }
            double qL = 0, qR = 0;
            foreach (var f in m.Faces)
                if (f.B < 0 && f.Tag == ShellMesh.TagHole) { double q = m.Thickness[f.A] * f.Length / f.DistAB * r.V[f.A]; if (f.Mid.X < 0) qL += q; else qR += q; }
            double lr = Math.Abs(qL - qR) / (qL + qR);
            var notes = new List<string>();
            if (miss > 0) notes.Add($"镜像缺 {miss} 格 ⇒ 判不了");
            if (!(vErr <= 1e-9)) notes.Add($"V 镜像差 {vErr:E2}");
            if (!(jErr <= 1e-9)) notes.Add($"J 镜像差 {jErr:E2}");
            if (!(lr <= 1e-9)) notes.Add($"左右出流差 {lr:E2}");
            if (!r.Converged || !r.HoleFaceDirichlet) notes.Add("没收敛或面上口径没生效");
            W($"h {h}：格 {m.CellCount}　镜像缺 {miss}　V 镜像最大差 {vErr:E2}　J 镜像最大差 {jErr:E2}　左/右孔面出流 {qL:R}/{qR:R} 相对差 {lr:E2}　守恒 {r.ConservationError:E2}　{(notes.Count == 0 ? "过" : "**" + string.Join("；", notes) + "**")}");
            if (notes.Count > 0) { bad++; badL.Add($"h{h}: " + string.Join("；", notes)); }
        }
        Save("F6a_门_双舌板孔边对称.txt", sb, _o);
        Assert.True(bad == 0, string.Join("　", badL));
    }
}
