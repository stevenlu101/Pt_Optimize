using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  决 101 A（2026-09-23，RING；业主 13:3x「其余先按路线甲」）：图纸（栅格）路径在孔圆 r = rh 的 ± 一个栅格步带内**按解析圆判料**（Core：HoleBandCircleField，
//  生成器 FlangeMesher.BuildFromMaterialWith 在 MeshRules.HoleBandCircle 开时把 ThicknessField 包一层；改回 = MeshRules.HoleBandCircle = false，只供门）。
//  依据：deliverable/R48_图纸对拍步0p5离群_归因_2026-09-23.md §5（G3 原型 B）；完整记录 deliverable/R48_孔环按解析圆判料_实施记录_2026-09-23.md。
//
//  门（阈值一个不挪；新阈值都写出处）：
//    a 精确积分对 0.005 mm 子采样原型（G3 探针 G3CircleSnapField 逐字抄在下面，keepRasterT = 原型 B）：两例 × 栅格步 1.0／0.5／0.25／0.1，碰带的每个格矩形
//      |面积差| ≤ B_A、|体积差| ≤ B_V。**误差界（数学推出）**：子采样是中点规则，子格 dx × dz（dx = W/⌈W/0.005⌉ ≤ 0.005，dz 同）；被积函数分段常数，
//      只有被间断曲线穿过的子格有误差，每个 ≤ dx·dz·J（J = 跳幅：面积 1，体积 t_max）。一条在 x、z 上都单调、跨度 Lx、Lz 的曲线段至多穿过 Lx/dx + Lz/dz + 2 个子格
//      ⇒ 误差 ≤ J·Σ_段 (Lx·dz + Lz·dx + 2·dx·dz)。间断曲线全集（定义里所有可能换值的地方）：三个圆 rh − b／rh／rh + b（每象限一段，跨度按矩形宽高 W、H 取上界）、
//      栅格半格线（竖线 Lx = 0、Lz = H；横线反之）、外移取厚的 p′ 两族曲线 p′_x = c、p′_z = d（c、d 为矩形 ± s 内的半格线；每条至多 2 个单调段，推导见 Core 注释）。
//      t_max = 矩形外扩 2s 内栅格节点厚度的最大值（p′ 离 p 一个 s，最近节点再差 s/√2 < s）。界是上界，偏松（实测差 ÷ 界 ≤ 0.001；厚度 ×1.2 的变异照样在界内）—— 所以另加**收敛**两条（步 1.0、0.1）：
//      ① 子采样加密一倍（0.0025 mm）后，逐格 |面积差|、|体积差| 之和都比 0.005 mm 时小（没有新常数）。⚠ 这条判不出系统差：差里有系统量 δ 时，子采样误差与 δ 同号的格
//        加密后照样「变小」一点（2026-09-23 审查 T1 变异核：PlateMaterial Shifted() 取厚 ×1.02／×1.2，这条与界都过）。
//      ② 2026-09-23 审查后加（T1）：Σ|I − P_0.0025| ≤ Σ|P_0.005 − P_0.0025|，面积、体积各一条，没有新常数。依据是**实测判据**，不是推出：子采样误差按 h¹ 以上收敛时
//        左边（新档误差）不大于两档之差；逐格不严格（分段常数被积函数的中点规则逐格误差不随 h 单调），实测基线余量约 3.7～3.9 倍。
//        精确积分差一个系统量 δ、且 δ 大于两档子采样之差时判红（变异核：×1.02 与 ×1.2 八组全红，例如 ×1.02 盘Ø56 步 1 体积 6.16E-1 > 1.70E-2）。
//      面积另有闭式核（门 b：对「矩形 − 矩形∩圆盘」差 ≤ 1e-9）。
//    b 带内料形对解析圆：两例 × 栅格步 1.0／0.5／0.1（§0.-20 丙 的三档）：
//      · 孔圆穿过的格 FractionInsideCircle(…, rh)（生产入口 FlangeMesher.MaterialFractionInCircle）≤ 1e-12（舍入级：带内料的起点与裁剪用同一个 √(rh² − x²)，应恰为 0）；
//        ⚠ 这一条对改回牙很弱：栅格的圆内份额按方格中心判，孔内节点都没料 ⇒ 步 1.0／0.5 改回也是 0（没牙）；步 0.1 改回印出 0.01，这一档改回会红
//        （0.01 的来源推断是恰落在孔圆 r = rh 上的有料节点被判进圆内，没核）。输出串按改回实际值写（2026-09-23 审查 T3 改：原先写死「改回也为 0」）。主要的牙在下两条；
//      · 孔圆穿过的格边上孔圆内侧（带内）有料长度（FlangeMesher.HoleInsideMaxMm，= 配方「量得」那一项）≤ ShellMesh.GeomTolMm（沿用：建面边长容差）；改回 &gt; GeomTolMm（改回 ⇒ 红）；
//      · 碰带的格矩形上有料面积 = 闭式「矩形 − 矩形∩圆盘(rh)」（测试侧按 ∫√(rh² − x²) dx 的原函数独立算），|差| ≤ 1e-9 mm²（选定：GL16 在解析光滑段上的舍入级余量）。
//        成立条件 = 图纸孔径与 rh 相同、带宽 ≥ s/√2、矩形里没有别的轮廓边；最后一条逐矩形核：解析板（AnalyticMaterial）的面积与同一闭式差 ≤ 1e-9 才算「没有别的轮廓边」，
//        不满足的（例如盘Ø56 步 1.0 碰带矩形伸到盘缘 R = 28 的栅格台阶）跳过并印个数 —— 那是带外栅格的事，不归本门；
//      · §0.-20 孔弧覆盖率 |1 − 覆盖率| ≤ 1e-9（沿用 R48ArcGapDiagTests 门 b 的浮点余量）、缺口 0 段。改回在这两例三档上本来也是 1（§0.-20 丙），牙在 R48ArcGapDiagTests 门 a（W08 R31 w30 改回 2.141 mm、修后 0）。
//    c 三入口同一份材料（守恒关系）：矩形 A 对 x1 的导数 = 右边线上的有料长度（条带 [x − ε, x + ε]：Integrate.Area/(2ε) = SegmentMaterial.Length，z 矩同理，横向同理），
//      矩形二分可加、FractionInsideCircle(rh) = 0、FractionInsideCircle(R)·A = 解析板同一量（R = rh + b/4：带内的点按 r ≤ R 精确判；带外栅格料 r ≥ rh + b 的节点半径 ≥ rh + b − s/√2 > R（b = s 时 rh + 0.293 s），
//      按方格中心判也在圆外 ⇒ 两种判法在这个 R 上一致，才能与解析板的精确裁剪比；R 取到 rh + b/2 就会有带外方格的节点落进圆内，第一版这样写过、当场不等 5e-4 mm²）、R 超出矩形 = 1。
//      条带容差 = ε·(|L′| 的有限差分估计 × 2 + 1) + 1e-9（推出：L 在条带内 C¹ 时 |∫L/(2ε) − L(x)| ≤ ε·sup|L′|/2；×2 留给估计误差与条带里的折点）；可加性 1e-10·max(1, |值|)（选定：舍入余量）。
//    d 相位包络（慢）：照 G3 探针 ZZG3DrawParityProbe6Tests 的 x 相位平移（**照抄原栅格的原点与格数**，平移为 0 的 z 向逐位同 Rasterize），两例 × 栅格步 1.0／0.5／0.25／0.1 × 8 个 x 相位，
//      「图纸 − 生产 Build」抽热差，判三条（2026-09-23 审查 T2 后分开写来源）：
//        · max|Δ| 与包络宽度（max − min）都随步长不增 —— 这两条是现行 DrawingPathParityTests:121「随步长收窄」判法推到多相位（单调比较留 1e-9 W 浮点余量，同原门）；
//        · 每例全部 32 个样本符号单一 —— **本门新加的判据（选定）**，DrawingPathParityTests 里没有符号判据；没有推导（步长趋于 0 时差趋于 0，符号单一本身不是收敛性质），
//          没有数值阈值，按严格同号判（x < 0 或 x > 0，没有近 0 余量）。现有余量：修后盘Ø56 步 0.1 最大值 −0.0004 W，贴近 0。
//      改回的包络同样印出并断言它违反上面至少一条（改回 ⇒ 红）。⚠ 实测改回只违反「符号单一」这一条（证据 …相位包络_…_152035.txt：改回两例 max|Δ| 与宽都单调收窄），
//        即门 d「改回 ⇒ 红」的唯一来源是这条新判据。要不要在相位 0 上复用原判法补牙、或给符号判据加近 0 处理 —— 改判据口径，【待决定】，本门没动。
//    e 配方：改回的图纸网格规则与生产恰好只差「孔带按解析圆判料」两项；带宽 s/√2 的网格规则等于生产（带宽不进规则，记在 HoleBandMm）。
//    f 浮空料块必须量得并报出（2026-09-23 审查 M1 后加）：合成盘（rh 12.9、R 25、焊脚 1.2、栅格步 0.5、相位 (0.13, 0.07)、细带 1.0／内带 0.25，逐字照审查探针 probe_ring_math 的建法），
//      图纸孔半径 = rh、rh − 0.75、rh + 1.0 三例 × 修后／改回：配方的连通分量数、浮空分量数、浮空面积 = 测试侧另写的并查集（长度 > 0 的内部面相连；锚 = 管孔边界面 ∪ 压接格）逐位相等；
//      浮空 > 0 ⇒ 配方说明句带「⚠」、整线说明（LineRunner.HoleArcDrawingNotes）带浮空句；三例里至少一例修后浮空 > 0（不空守：审查实测 −0.75 修后 6 个分量、+1.0 修后 53 个）。
//      无数值阈值（只有「长度 > 0」「> 0」）。不覆盖：浮空料块怎么处理（剔除、改规则）——【待决定】决 98；实板与真 .3dm 上有没有；热解数值（审查探针另量过，只印在实施记录）。
//
//  覆盖：两例（Builtin[0] 片0、盘Ø56 片1）的解析板替身栅格（图纸孔径 = rh）；x 向相位；三个入口在碰带矩形上的一致；改回逐位（见实施记录的改前／改回对拍）。
//  不覆盖：真 .3dm 的栅格（Rhino 探针，Windows）；z 向相位（会让舌直边 ±w 离开节点，另一件事，同 G3）；锥形舌与带开孔／开槽的板（带内若有别的轮廓边，门 b 面积相等那条的成立条件不满足）；
//        图纸孔径 ≠ rh 时的判料是否「对」（失配时参照本身不适定，F3 A1(6)）—— 只在归因档里实测代价（吸收多少），不判；失配切出的浮空料块只由门 f 管「报没报出」，不管该不该有。
//        门 a 只在 x 相位 0（Rasterize 不平移）上与原型比；非零相位上碰带格的材料没与原型比（门 d 只比抽热包络）。
// ════════════════════════════════════════════════════════════════════════════
public class R48HoleBandCircleGateTests
{
    private readonly ITestOutputHelper _o;
    public R48HoleBandCircleGateTests(ITestOutputHelper o) { _o = o; }

    internal static readonly MeshRules Off = new() { HoleBandCircle = false };                  // 改回：老栅格
    /// <summary>带真实符号的 4 位小数（「+0.000;-0.000」格式在负数舍到 0 时会印出「-+0.000」）。</summary>
    static string S(double v) => (v < 0 || (v == 0 && double.IsNegative(v)) ? "-" : "+") + Math.Abs(v).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
    internal static readonly MeshRules BandSqrt2 = new() { HoleBandPerStep = 1 / Math.Sqrt(2) }; // 带宽 s/√2（决 98 的另一把尺）

    internal static (FlangePlate g, LineCase lc, double iA, double tRoot, double tSet, double clampC, double tabInsul, string name) Case(int which)
        => DrawingPathParityTests.Case(which);

    /// <summary>
    /// 图纸网格：rules = null 走生产入口 BuildFromField（带内按解析圆判料）；否则经 BuildFromMaterialWith 传规则（锚点同样从栅格推）。
    /// 压接三开关显式钉成现行生产值（细带 0、整面接触、面上定温）：本档写 deliverable 证据，受 R48ClampRecipeTests.g 管（生产缺省一改，证据探针不许悄悄换口径）。
    /// </summary>
    internal static ShellMesh Mesh(ThicknessField tf, FlangePlate g, LineCase lc, MeshRules? rules, double scale = 1.0)
    {
        double hF = lc.MeshFineMm * scale, hC = lc.MeshCoarseMm * scale;
        return rules is null
            ? FlangeMesher.BuildFromField(tf, g.HoleRadiusMm, 0, hF, hC, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true)
            : FlangeMesher.BuildFromMaterialWith(tf, g.HoleRadiusMm, rules, 0, hF, hC, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
    }

    /// <summary>x 相位平移栅格 —— 逐字照抄 G3 探针 ZZG3DrawParityProbe6Tests.Shift（oz = 0 的那一支）：平移为 0 的 z 向照抄原栅格的原点与格数（坐标逐位同 Rasterize）。</summary>
    internal static ThicknessField ShiftX(FlangePlate g, double step, double ox)
    {
        var f0 = FlangeMesher.Rasterize(g, step, 2.0);
        if (ox == 0) return f0;
        int nx = f0.Nx + 1, nz = f0.Nz;
        double x0 = f0.X0 - step + ox, z0 = f0.Z0;
        var f = new ThicknessField { X0 = x0, Z0 = z0, Step = step, Nx = nx, Nz = nz, T = new double[nx * nz],
            XMinMaterial = f0.XMinMaterial, XMaxMaterial = f0.XMaxMaterial, ZMinMaterial = f0.ZMinMaterial, ZMaxMaterial = f0.ZMaxMaterial };
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                double x = x0 + i * step, z = z0 + j * step;
                f.T[i * nz + j] = g.Inside(x, z) ? g.ThicknessAt(x, z) : 0.0;
            }
        return f;
    }

    /// <summary>
    /// G3 原型 B 的子采样材料场 —— 逐字抄自 deliverable/R48_图纸对拍步0p5离群_归因_证据_2026-09-23/探针源码_ZZG3DrawParityProbeTests.cs.txt 的 G3CircleSnapField（keepRasterT = true 那一支；
    /// FractionInsideCircle 原型转调栅格，这里用不到）。只作门 a 的参照。
    /// </summary>
    internal sealed class SubsampleProto
    {
        readonly ThicknessField _f; readonly double _rh, _b, _sub;
        public SubsampleProto(ThicknessField f, double rh, double band, double sub = 0.005) { _f = f; _rh = rh; _b = band; _sub = sub; }
        public bool Touch(double x0, double x1, double z0, double z1)
        {
            double nx = Math.Clamp(0, x0, x1), nz = Math.Clamp(0, z0, z1);
            double near = Math.Sqrt(nx * nx + nz * nz);
            double fx = Math.Max(Math.Abs(x0), Math.Abs(x1)), fz = Math.Max(Math.Abs(z0), Math.Abs(z1));
            double far = Math.Sqrt(fx * fx + fz * fz);
            return near < _rh + _b && far > _rh - _b;
        }
        double T(double x, double z)
        {
            double r = Math.Sqrt(x * x + z * z);
            if (Math.Abs(r - _rh) >= _b) return _f.At(x, z);
            if (r < _rh) return 0;
            double t0 = _f.At(x, z);
            if (t0 > 1e-9) return t0;
            double r1 = r + _f.Step;   // 栅格这点没料：沿射线往外一个栅格步取
            return r > 1e-12 ? _f.At(x * r1 / r, z * r1 / r) : 0;
        }
        /// <summary>返回（面积、体积、子格 dx、dz）。</summary>
        public (double A, double V, double Dx, double Dz) Integrate(double x0, double x1, double z0, double z1)
        {
            if (!Touch(x0, x1, z0, z1)) { var r = _f.Integrate(x0, x1, z0, z1); return (r.Area, r.Volume, 0, 0); }
            int nx = Math.Max(1, (int)Math.Ceiling((x1 - x0) / _sub)), nz = Math.Max(1, (int)Math.Ceiling((z1 - z0) / _sub));
            double dx = (x1 - x0) / nx, dz = (z1 - z0) / nz, a = dx * dz, A = 0, V = 0;
            for (int i = 0; i < nx; i++)
            {
                double x = x0 + (i + 0.5) * dx;
                for (int j = 0; j < nz; j++)
                {
                    double z = z0 + (j + 0.5) * dz;
                    double t = T(x, z);
                    if (t <= 1e-9) continue;
                    A += a; V += a * t;
                }
            }
            return (A, V, dx, dz);
        }
    }

    /// <summary>门 a 的子采样误差界里「Σ_段 (Lx·dz + Lz·dx + 2·dx·dz)」（J = 1 那部分）；式子见文件头。</summary>
    internal static double CurveSum(double x0, double x1, double z0, double z1, double rh, double b, double s, double X0, double Z0, double dx, double dz, bool shift)
    {
        double W = x1 - x0, H = z1 - z0;
        double Per(double lx, double lz) => lx * dz + lz * dx + 2 * dx * dz;
        double sum = 0;
        // 三个圆：与矩形相交就按「矩形跨了几个象限」段、每段跨度取 W、H
        int quads = (x0 < 0 && x1 > 0 ? 2 : 1) * (z0 < 0 && z1 > 0 ? 2 : 1);
        double nx = Math.Clamp(0, x0, x1), nz = Math.Clamp(0, z0, z1);
        double near = Math.Sqrt(nx * nx + nz * nz), far = Math.Sqrt(Math.Pow(Math.Max(Math.Abs(x0), Math.Abs(x1)), 2) + Math.Pow(Math.Max(Math.Abs(z0), Math.Abs(z1)), 2));
        foreach (double rho in new[] { rh - b, rh, rh + b })
            if (rho > 0 && near < rho && far > rho) sum += quads * Per(W, H);
        // 栅格半格线
        int nV = 0, nH = 0;
        for (int k = (int)Math.Ceiling((x0 - X0) / s - 0.5); k <= (int)Math.Floor((x1 - X0) / s - 0.5); k++) nV++;
        for (int k = (int)Math.Ceiling((z0 - Z0) / s - 0.5); k <= (int)Math.Floor((z1 - Z0) / s - 0.5); k++) nH++;
        sum += nV * Per(0, H) + nH * Per(W, 0);
        // p′ 两族：c ∈ [x0 − s, x1 + s]、d ∈ [z0 − s, z1 + s] 的半格线，每条至多 2 个单调段
        if (shift)
        {
            int nc = 0, nd = 0;
            for (int k = (int)Math.Ceiling((x0 - s - X0) / s - 0.5); k <= (int)Math.Floor((x1 + s - X0) / s - 0.5); k++) nc++;
            for (int k = (int)Math.Ceiling((z0 - s - Z0) / s - 0.5); k <= (int)Math.Floor((z1 + s - Z0) / s - 0.5); k++) nd++;
            sum += (nc + nd) * 2 * Per(W, H);
        }
        return sum;
    }

    static double TMaxNear(ThicknessField f, double x0, double x1, double z0, double z1)
    {
        double s = f.Step, t = 0;
        int i0 = Math.Max(0, (int)Math.Floor((x0 - 2 * s - f.X0) / s)), i1 = Math.Min(f.Nx - 1, (int)Math.Ceiling((x1 + 2 * s - f.X0) / s));
        int j0 = Math.Max(0, (int)Math.Floor((z0 - 2 * s - f.Z0) / s)), j1 = Math.Min(f.Nz - 1, (int)Math.Ceiling((z1 + 2 * s - f.Z0) / s));
        for (int i = i0; i <= i1; i++) for (int j = j0; j <= j1; j++) t = Math.Max(t, f.T[i * f.Nz + j]);
        return t;
    }

    static IEnumerable<(double x0, double x1, double z0, double z1)> Rects(ShellMesh m)
    {
        for (int c = 0; c < m.CellCount; c++)
        {
            var rl = m.CellRects != null && c < m.CellRects.Length && m.CellRects[c] != null ? m.CellRects[c] : new List<(double, double, double, double)> { FlangeMesher.CellRect(m, c) };
            foreach (var r in rl) yield return r;
        }
    }

    /// <summary>闭式：矩形 [x0,x1]×[z0,z1] ∩ 圆盘 r ≤ R 的面积（∫ max(0, min(z1, h) − max(z0, −h)) dx，h = √(R² − x²)；按 h = |z0|、|z1| 与 ±R 分段，段内用原函数 ½(x·h + R²·asin(x/R))）。</summary>
    internal static double RectDiskArea(double x0, double x1, double z0, double z1, double R)
    {
        double F(double x) { double xc = Math.Clamp(x, -R, R); return 0.5 * (xc * Math.Sqrt(Math.Max(0, R * R - xc * xc)) + R * R * Math.Asin(xc / R)); }   // ∫ h dx
        var bp = new List<double> { x0, x1 };
        foreach (double zc in new[] { z0, z1 }) if (Math.Abs(zc) < R) { double w = Math.Sqrt(R * R - zc * zc); bp.Add(w); bp.Add(-w); }
        bp.Add(R); bp.Add(-R);
        var xs = bp.Where(v => v >= x0 && v <= x1).OrderBy(v => v).ToList();
        double area = 0;
        for (int k = 0; k + 1 < xs.Count; k++)
        {
            double a = xs[k], b = xs[k + 1];
            if (!(b > a)) continue;
            double xm = 0.5 * (a + b);
            if (Math.Abs(xm) >= R) continue;
            double h = Math.Sqrt(R * R - xm * xm);
            double hi = Math.Min(z1, h), lo = Math.Max(z0, -h);
            if (!(hi > lo)) continue;
            double iHi = z1 <= h ? z1 * (b - a) : F(b) - F(a);      // ∫ min(z1, h)
            double iLo = z0 >= -h ? z0 * (b - a) : -(F(b) - F(a));  // ∫ max(z0, −h)
            area += iHi - iLo;
        }
        return area;
    }

    static bool Crosses((double x0, double x1, double z0, double z1) r, double rh)
    {
        double nx = Math.Clamp(0, r.x0, r.x1), nz = Math.Clamp(0, r.z0, r.z1);
        double fx = Math.Max(Math.Abs(r.x0), Math.Abs(r.x1)), fz = Math.Max(Math.Abs(r.z0), Math.Abs(r.z1));
        return nx * nx + nz * nz < rh * rh && fx * fx + fz * fz > rh * rh;
    }

    // ───────────────────────────── a ─────────────────────────────
    [Fact]
    public void 门a_精确积分对子采样原型_碰带格的面积体积差在子采样误差界内()
    {
        foreach (int which in new[] { 1, 0 })
        {
            var (g, lc, _, _, _, _, _, name) = Case(which);
            double rh = g.HoleRadiusMm;
            foreach (double step in new[] { 1.0, 0.5, 0.25, 0.1 })
            {
                var tf = FlangeMesher.Rasterize(g, step, 2.0);
                var m = Mesh(tf, g, lc, null);
                var hb = Assert.IsType<HoleBandCircleField>(m.Material);
                Assert.Equal(step, hb.BandMm);   // 生产带宽 = 1 × 栅格步
                var proto = new SubsampleProto(tf, rh, hb.BandMm);
                int n = 0; double sA = 0, sP = 0, sV = 0, sPV = 0, worstA = 0, worstV = 0, maxDA = 0, maxDV = 0;
                foreach (var r in Rects(m))
                {
                    if (!hb.Touch(r.x0, r.x1, r.z0, r.z1)) continue;
                    n++;
                    var I = hb.Integrate(r.x0, r.x1, r.z0, r.z1);
                    var P = proto.Integrate(r.x0, r.x1, r.z0, r.z1);
                    double cs = CurveSum(r.x0, r.x1, r.z0, r.z1, rh, hb.BandMm, step, tf.X0, tf.Z0, P.Dx, P.Dz, shift: true);
                    double bA = cs, bV = cs * TMaxNear(tf, r.x0, r.x1, r.z0, r.z1);
                    double dA = Math.Abs(I.Area - P.A), dV = Math.Abs(I.Volume - P.V);
                    Assert.True(dA <= bA, $"{name} 步 {step} 矩形 [{r.x0},{r.x1}]×[{r.z0},{r.z1}]：面积 精确 {I.Area:R} 子采样 {P.A:R} 差 {dA:E3} > 界 {bA:E3}");
                    Assert.True(dV <= bV, $"{name} 步 {step} 矩形 [{r.x0},{r.x1}]×[{r.z0},{r.z1}]：体积 精确 {I.Volume:R} 子采样 {P.V:R} 差 {dV:E3} > 界 {bV:E3}");
                    sA += I.Area; sP += P.A; sV += I.Volume; sPV += P.V;
                    maxDA = Math.Max(maxDA, dA); maxDV = Math.Max(maxDV, dV);
                    if (bA > 0) worstA = Math.Max(worstA, dA / bA);
                    if (bV > 0) worstV = Math.Max(worstV, dV / bV);
                }
                _o.WriteLine($"{name} 栅格步 {step}：碰带矩形 {n} 个；Σ面积 精确 {sA:0.000000} 子采样 {sP:0.000000}（差 {sA - sP:+0.0000E+0;-0.0000E+0} mm²）；Σ体积 精确 {sV:0.000000} 子采样 {sPV:0.000000}（差 {sV - sPV:+0.0000E+0;-0.0000E+0} mm³）；"
                           + $"逐格 max|Δ面积| {maxDA:E2}（÷界最大 {worstA:0.000}）、max|Δ体积| {maxDV:E2}（÷界最大 {worstV:0.000}）");
                Assert.True(n > 0);
                // 收敛：子采样 0.005 → 0.0025，逐格 |差| 之和变小（栅格步 1.0 与 0.1 两档，其余档同一做法、省机时）
                if (step == 1.0 || step == 0.1)
                {
                    var proto2 = new SubsampleProto(tf, rh, hb.BandMm, sub: 0.0025);
                    double eA1 = 0, eV1 = 0, eA2 = 0, eV2 = 0, dA12 = 0, dV12 = 0;
                    foreach (var r in Rects(m))
                    {
                        if (!hb.Touch(r.x0, r.x1, r.z0, r.z1)) continue;
                        var I = hb.Integrate(r.x0, r.x1, r.z0, r.z1);
                        var P1 = proto.Integrate(r.x0, r.x1, r.z0, r.z1); var P2 = proto2.Integrate(r.x0, r.x1, r.z0, r.z1);
                        eA1 += Math.Abs(I.Area - P1.A); eV1 += Math.Abs(I.Volume - P1.V); eA2 += Math.Abs(I.Area - P2.A); eV2 += Math.Abs(I.Volume - P2.V);
                        dA12 += Math.Abs(P1.A - P2.A); dV12 += Math.Abs(P1.V - P2.V);   // 两档子采样之差（审查 T1 后加）
                    }
                    _o.WriteLine($"    收敛：Σ|Δ面积| 子采样 0.005 {eA1:E3} → 0.0025 {eA2:E3}（比 {eA2 / eA1:0.000}）；Σ|Δ体积| {eV1:E3} → {eV2:E3}（比 {eV2 / eV1:0.000}）");
                    _o.WriteLine($"    收敛②：Σ|I − P_0.0025| 对 Σ|P_0.005 − P_0.0025|：面积 {eA2:E3} {(eA2 <= dA12 ? "≤" : ">")} {dA12:E3}（比 {dA12 / eA2:0.0}）；体积 {eV2:E3} {(eV2 <= dV12 ? "≤" : ">")} {dV12:E3}（比 {dV12 / eV2:0.0}）");
                    Assert.True(eA2 < eA1 && eV2 < eV1, $"{name} 步 {step}：子采样加密后对精确积分的差没变小（面积 {eA1:E3}→{eA2:E3}，体积 {eV1:E3}→{eV2:E3}）");
                    Assert.True(eA2 <= dA12 && eV2 <= dV12, $"{name} 步 {step}：精确积分对细档子采样的差大于两档子采样之差（面积 {eA2:E3} > {dA12:E3} 或 体积 {eV2:E3} > {dV12:E3}）—— 差里有系统量");
                }
            }
        }
    }

    // ───────────────────────────── b ─────────────────────────────
    [Fact]
    public void 门b_带内料形对解析圆_孔圆内无料_面积等于解析板_孔弧覆盖率为1()
    {
        foreach (int which in new[] { 1, 0 })
        {
            var (g, lc, _, _, _, _, _, name) = Case(which);
            double rh = g.HoleRadiusMm;
            var an = new AnalyticMaterial(g);
            foreach (double step in new[] { 1.0, 0.5, 0.1 })
            {
                var tf = FlangeMesher.Rasterize(g, step, 2.0);
                var mOn = Mesh(tf, g, lc, null);
                var mOff = Mesh(tf, g, lc, Off);
                var hb = Assert.IsType<HoleBandCircleField>(mOn.Material);
                Assert.Same(tf, mOn.SourceField);   // 包层不改 SourceField（图纸孔径核对读的仍是图纸本身）
                Assert.Same(tf, mOff.Material);
                // 孔圆穿过的格：圆内份额
                double fOn = 0, fOff = 0; int nCross = 0;
                for (int c = 0; c < mOn.CellCount; c++)
                    if ((mOn.CellRects?[c] ?? new List<(double, double, double, double)> { FlangeMesher.CellRect(mOn, c) }).Any(r => Crosses(r, rh)))
                    { nCross++; double fr = FlangeMesher.MaterialFractionInCircle(mOn, c, rh); if (!double.IsNaN(fr)) fOn = Math.Max(fOn, fr); }
                for (int c = 0; c < mOff.CellCount; c++)
                    if ((mOff.CellRects?[c] ?? new List<(double, double, double, double)> { FlangeMesher.CellRect(mOff, c) }).Any(r => Crosses(r, rh)))
                    { double fr = FlangeMesher.MaterialFractionInCircle(mOff, c, rh); if (!double.IsNaN(fr)) fOff = Math.Max(fOff, fr); }
                double inOn = FlangeMesher.HoleInsideMaxMm(mOn, mOn.Material!, rh), inOff = FlangeMesher.HoleInsideMaxMm(mOff, mOff.Material!, rh);
                // 碰带矩形：面积 = 闭式 矩形 − 矩形∩圆盘(rh)（矩形里没有别的轮廓边的才比：解析板面积与闭式差 ≤ 1e-9）
                double maxDA = 0; int nBand = 0, nSkip = 0;
                foreach (var r in Rects(mOn))
                {
                    if (!hb.Touch(r.x0, r.x1, r.z0, r.z1)) continue;
                    double exact = (r.x1 - r.x0) * (r.z1 - r.z0) - RectDiskArea(r.x0, r.x1, r.z0, r.z1, rh);
                    if (!(Math.Abs(an.Integrate(r.x0, r.x1, r.z0, r.z1).Area - exact) <= 1e-9)) { nSkip++; continue; }
                    nBand++;
                    maxDA = Math.Max(maxDA, Math.Abs(hb.Integrate(r.x0, r.x1, r.z0, r.z1).Area - exact));
                }
                string offTeeth = fOff <= 1e-12 ? "改回也为 0，这一档这一条对改回没牙" : $"改回 {fOff:E2} > 1e-12，这一档这一条改回会红";   // 审查 T3：按实际值写，不写死
                _o.WriteLine($"{name} 栅格步 {step}：孔圆穿过的格 {nCross}；圆内份额最大 修后 {fOn:E2}／改回 {fOff:E2}（{offTeeth}）；"
                           + $"格边孔圆内侧有料 修后 {inOn:E2} mm／改回 {inOff:0.0000} mm；碰带矩形 {nBand} 个（另 {nSkip} 个有别的轮廓边，跳过），|面积 − 闭式| 最大 {maxDA:E2} mm²；"
                           + $"覆盖率 修后 {mOn.HoleArcCoverage:R}（{mOn.HoleArcGapCount} 段）／改回 {mOff.HoleArcCoverage:R}（{mOff.HoleArcGapCount} 段）");
                Assert.True(fOn <= 1e-12, $"{name} 步 {step}：孔圆穿过的格圆内份额 {fOn:E3} > 1e-12");
                Assert.True(inOn <= ShellMesh.GeomTolMm, $"{name} 步 {step}：修后格边孔圆内侧有料 {inOn:E3} mm");
                Assert.True(inOff > ShellMesh.GeomTolMm, $"{name} 步 {step}：改回格边孔圆内侧没有料 —— 这一条对改回也没牙");
                Assert.True(nBand > 0 && maxDA <= 1e-9, $"{name} 步 {step}：碰带矩形面积对闭式差 {maxDA:E3} > 1e-9 mm²（比了 {nBand} 个）");
                Assert.True(Math.Abs(mOn.HoleArcCoverage - 1) <= 1e-9 && mOn.HoleArcGapCount == 0, $"{name} 步 {step}：修后覆盖率 {mOn.HoleArcCoverage:R}、{mOn.HoleArcGapCount} 段");
                Assert.True(mOn.Recipe!.HoleBandCircle && mOn.Recipe.HoleBandCircleSwitch && mOn.Recipe.HoleBandMm == step);
            }
        }
    }

    // ───────────────────────────── c ─────────────────────────────
    [Fact]
    public void 门c_三入口同一份材料_条带面积导数等于线段有料长_可加_圆内份额对解析板()
    {
        var rnd = new Random(20260923);
        foreach (int which in new[] { 1, 0 })
        {
            var (g, lc, _, _, _, _, _, name) = Case(which);
            double rh = g.HoleRadiusMm;
            var an = new AnalyticMaterial(g);
            foreach (double step in new[] { 0.5, 0.1 })
            {
                var tf = FlangeMesher.Rasterize(g, step, 2.0);
                var m = Mesh(tf, g, lc, null);
                var hb = Assert.IsType<HoleBandCircleField>(m.Material);
                double b = hb.BandMm;
                var rects = Rects(m).Where(r => hb.Touch(r.x0, r.x1, r.z0, r.z1)).ToList();
                double worstStrip = 0, worstAdd = 0, worstFrac = 0; int nStrip = 0;
                foreach (var r in rects)
                {
                    var I = hb.Integrate(r.x0, r.x1, r.z0, r.z1);
                    // 可加：x、z 各二分一次
                    double xm = r.x0 + 0.437 * (r.x1 - r.x0), zm = r.z0 + 0.563 * (r.z1 - r.z0);
                    foreach (var (p, q) in new[] { (hb.Integrate(r.x0, xm, r.z0, r.z1), hb.Integrate(xm, r.x1, r.z0, r.z1)), (hb.Integrate(r.x0, r.x1, r.z0, zm), hb.Integrate(r.x0, r.x1, zm, r.z1)) })
                    {
                        foreach (var (whole, parts) in new[] { (I.Area, p.Area + q.Area), (I.Volume, p.Volume + q.Volume), (I.MomentX, p.MomentX + q.MomentX), (I.MomentZ, p.MomentZ + q.MomentZ) })
                        {
                            double d = Math.Abs(whole - parts) / Math.Max(1, Math.Abs(whole));
                            worstAdd = Math.Max(worstAdd, d);
                            Assert.True(d <= 1e-10, $"{name} 步 {step}：矩形 [{r.x0},{r.x1}]×[{r.z0},{r.z1}] 二分不可加：整 {whole:R} 分 {parts:R}");
                        }
                    }
                    // 条带：竖（x 固定）与横（z 固定）各两处
                    for (int k = 0; k < 2; k++)
                    {
                        foreach (bool vertical in new[] { true, false })
                        {
                            double lo = vertical ? r.x0 : r.z0, hi = vertical ? r.x1 : r.z1;
                            double u = lo + (0.15 + 0.7 * rnd.NextDouble()) * (hi - lo);
                            const double eps = 1e-6, del = 1e-4;
                            MaterialIntegral Strip(double c, double e) => vertical ? hb.Integrate(c - e, c + e, r.z0, r.z1) : hb.Integrate(r.x0, r.x1, c - e, c + e);
                            (double Length, double Mid) Seg(double c) => vertical ? hb.SegmentMaterial(true, c, r.z0, r.z1) : hb.SegmentMaterial(false, c, r.x0, r.x1);
                            var st = Strip(u, eps); var sg = Seg(u);
                            double slope = Math.Abs(Seg(u + del).Length - Seg(u - del).Length) / (2 * del);
                            double tol = eps * (2 * slope + 1) + 1e-9;
                            double lenS = st.Area / (2 * eps), momS = (vertical ? st.MomentZ : st.MomentX) / (2 * eps);
                            double dL = Math.Abs(lenS - sg.Length), dM = Math.Abs(momS - sg.Length * sg.Mid);
                            double scaleM = Math.Max(Math.Abs(vertical ? r.z0 : r.x0), Math.Abs(vertical ? r.z1 : r.x1));
                            worstStrip = Math.Max(worstStrip, dL / tol);
                            nStrip++;
                            Assert.True(dL <= tol, $"{name} 步 {step}：{(vertical ? "竖" : "横")}条带 {u:R}：Integrate 面积/2ε {lenS:R} 对 SegmentMaterial 长 {sg.Length:R}，差 {dL:E3} > 容差 {tol:E3}");
                            Assert.True(dM <= tol * scaleM, $"{name} 步 {step}：{(vertical ? "竖" : "横")}条带 {u:R}：一次矩 {momS:R} 对 长×中点 {sg.Length * sg.Mid:R}，差 {dM:E3} > {tol * scaleM:E3}");
                        }
                    }
                    // 圆内份额：rh ⇒ 0；带内半径 R = rh + b/2 ⇒ 份额×面积 = 解析板；R 超出矩形 ⇒ 1
                    if (I.Area > 0)
                    {
                        double f0 = hb.FractionInsideCircle(r.x0, r.x1, r.z0, r.z1, rh);
                        Assert.True(f0 <= 1e-12, $"{name} 步 {step}：圆内份额(rh) = {f0:E3}");
                        double R = rh + 0.25 * b;
                        double aW = hb.FractionInsideCircle(r.x0, r.x1, r.z0, r.z1, R) * I.Area;
                        var IA = an.Integrate(r.x0, r.x1, r.z0, r.z1);
                        double aA = IA.Area > 0 ? an.FractionInsideCircle(r.x0, r.x1, r.z0, r.z1, R) * IA.Area : 0;
                        worstFrac = Math.Max(worstFrac, Math.Abs(aW - aA));
                        Assert.True(Math.Abs(aW - aA) <= 1e-9, $"{name} 步 {step}：带内圆 R = {R} 内的有料面积 包层 {aW:R} 解析板 {aA:R}");
                        Assert.Equal(1.0, hb.FractionInsideCircle(r.x0, r.x1, r.z0, r.z1, 1e3));
                    }
                }
                _o.WriteLine($"{name} 栅格步 {step}：碰带矩形 {rects.Count} 个；可加性相对差最大 {worstAdd:E2}；条带 {nStrip} 处，差÷容差最大 {worstStrip:0.000}；带内圆 R = rh + b/4 内面积对解析板最大差 {worstFrac:E2} mm²");
            }
        }
    }

    // ───────────────────────────── d（慢）─────────────────────────────
    internal sealed record Env(double Step, double[] On, double[] Off, double[] CovOn, double[] CovOff);

    [Trait("速度", "慢")]
    [Fact]
    public void 门d_相位包络_图纸减生产Build抽热差_符号单一且随步长收窄_改回红()
    {
        var steps = new[] { 1.0, 0.5, 0.25, 0.1 };
        const int NPhase = 8;
        var sb = new StringBuilder();
        string file = DeliverableOut.Stamped("R48_孔环判料_相位包络_2026-09-23.txt");
        void W(string t) { sb.AppendLine(t); _o.WriteLine(t); File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); }
        W($"决 101 A 门 d：两例 × 栅格步 {string.Join("／", steps)} × {NPhase} 个 x 相位（ox = k·s/{NPhase}，照抄 G3 探针的平移：z 向原点与格数照抄原栅格）；图纸 − 生产 Build 抽热差 W　开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W("修后 = 生产入口 BuildFromField（带内按解析圆判料，带宽 1 × 栅格步）；改回 = BuildFromMaterialWith(MeshRules.HoleBandCircle = false)");
        var bad = new List<string>();
        foreach (int which in new[] { 1, 0 })
        {
            var (g, lc, iA, tRoot, tSet, clampC, tabInsul, name) = Case(which);
            double hF = lc.MeshFineMm, hC = lc.MeshCoarseMm, rF = lc.MeshFineRadiusMm, cl = lc.Base.BusbarClampLengthMm;
            var mA = FlangeMesher.Build(g, 0, hF, hC, rF, cl, lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
            var a = DrawingPathParityTests.SolveOne(mA, lc, g, iA, tRoot, tSet, clampC, tabInsul);
            W($"══ {name}：生产 Build 抽热 {a.QFromTube:0.0000} W");
            var envs = new List<Env>();
            foreach (double st in steps)
            {
                var on = new double[NPhase]; var off = new double[NPhase]; var cOn = new double[NPhase]; var cOff = new double[NPhase];
                for (int k = 0; k < NPhase; k++)
                {
                    var tf = ShiftX(g, st, k * st / NPhase);
                    var m1 = Mesh(tf, g, lc, null); var m0 = Mesh(tf, g, lc, Off);
                    on[k] = DrawingPathParityTests.SolveOne(m1, lc, g, iA, tRoot, tSet, clampC, tabInsul).QFromTube - a.QFromTube;
                    off[k] = DrawingPathParityTests.SolveOne(m0, lc, g, iA, tRoot, tSet, clampC, tabInsul).QFromTube - a.QFromTube;
                    cOn[k] = m1.HoleArcCoverage; cOff[k] = m0.HoleArcCoverage;
                }
                envs.Add(new Env(st, on, off, cOn, cOff));
                W($"  步 {st}：修后 [{S(on.Min())}, {S(on.Max())}] 宽 {on.Max() - on.Min():0.0000} max|Δ| {on.Max(Math.Abs):0.0000}（覆盖率最小 {cOn.Min():0.000}）｜改回 [{S(off.Min())}, {S(off.Max())}] 宽 {off.Max() - off.Min():0.0000} max|Δ| {off.Max(Math.Abs):0.0000}（覆盖率最小 {cOff.Min():0.000}）");
                W("      修后 " + string.Join("  ", on.Select((v, k) => $"ox {k * st / NPhase:0.####}:{S(v)}")));
                W("      改回 " + string.Join("  ", off.Select((v, k) => $"ox {k * st / NPhase:0.####}:{S(v)}(覆盖 {cOff[k]:0.000})")));
            }
            List<string> Judge(Func<Env, double[]> pick)
            {
                var v = new List<string>();
                var all = envs.SelectMany(pick).ToArray();
                if (!(all.All(x => x < 0) || all.All(x => x > 0))) v.Add($"符号不单一（{S(all.Min())}～{S(all.Max())}）");
                for (int i = 0; i + 1 < envs.Count; i++)
                {
                    double m0 = pick(envs[i]).Max(Math.Abs), m1 = pick(envs[i + 1]).Max(Math.Abs);
                    double w0 = pick(envs[i]).Max() - pick(envs[i]).Min(), w1 = pick(envs[i + 1]).Max() - pick(envs[i + 1]).Min();
                    if (m1 > m0 + 1e-9) v.Add($"max|Δ| 步 {envs[i].Step} {m0:0.000} → 步 {envs[i + 1].Step} {m1:0.000} 没收窄");
                    if (w1 > w0 + 1e-9) v.Add($"包络宽 步 {envs[i].Step} {w0:0.000} → 步 {envs[i + 1].Step} {w1:0.000} 没收窄");
                }
                return v;
            }
            var vOn = Judge(e => e.On); var vOff = Judge(e => e.Off);
            W($"  判：修后 {(vOn.Count == 0 ? "过（符号单一、max|Δ| 与包络宽随步长不增）" : "红：" + string.Join("；", vOn))}");
            W($"  判：改回 {(vOff.Count == 0 ? "没红（门空守）" : "红（改回 ⇒ 红，符合预期）：" + string.Join("；", vOff))}");
            bad.AddRange(vOn.Select(x => $"{name} 修后 {x}"));
            if (vOff.Count == 0) bad.Add($"{name} 改回没红 —— 门空守");
        }
        W($"证据：{file}");
        Assert.True(bad.Count == 0, string.Join("；", bad));
    }

    // ───────────────────────────── e ─────────────────────────────
    [Fact]
    public void 门e_配方_改回恰差孔带两项_带宽s除根2规则同生产()
    {
        var (g, lc, _, _, _, _, _, name) = Case(1);
        foreach (double step in new[] { 1.0, 0.5 })
        {
            var tf = FlangeMesher.Rasterize(g, step, 2.0);
            var mOn = Mesh(tf, g, lc, null); var mOff = Mesh(tf, g, lc, Off); var mR2 = Mesh(tf, g, lc, BandSqrt2);
            _o.WriteLine($"{name} 步 {step}：修后「{mOn.Recipe!.Describe()}」");
            _o.WriteLine($"{name} 步 {step}：改回「{mOff.Recipe!.Describe()}」");
            _o.WriteLine($"{name} 步 {step}：带宽 s/√2「{mR2.Recipe!.Describe()}」");
            Assert.Equal(FlangeMesher.ProductionMeshRule, mOn.Recipe.Rule);
            Assert.NotEqual(FlangeMesher.ProductionMeshRule, mOff.Recipe.Rule);
            Assert.False(mOff.Recipe.HoleBandCircleSwitch); Assert.False(mOff.Recipe.HoleBandCircle); Assert.Equal(0.0, mOff.Recipe.HoleBandMm);
            Assert.Equal(FlangeMesher.ProductionMeshRule, mOff.Recipe.Rule with { HoleBandCircle = true, HoleBandCircleSwitch = true });
            Assert.Equal(FlangeMesher.ProductionMeshRule, mR2.Recipe.Rule);
            Assert.Equal(step / Math.Sqrt(2), mR2.Recipe.HoleBandMm, 12);
            Assert.Contains("孔带按解析圆判料：开关开", mOn.Recipe.Describe());
        }
        // 解析路径：规则开关照抄（开），量得为真（精确圆），没包（带宽 0）
        var mA = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
        Assert.IsType<AnalyticMaterial>(mA.Material);
        Assert.True(mA.Recipe!.HoleBandCircle && mA.Recipe.HoleBandCircleSwitch);
        Assert.Equal(0.0, mA.Recipe.HoleBandMm);
    }

    // ───────────────────────────── f（审查 M1 后加）─────────────────────────────
    /// <summary>合成盘栅格：逐字照审查探针 scratchpad/probe_ring_math/Program.cs 的 Plate（孔半径 rd、盘半径 R、焊脚宽 leg：孔边 1.6 线性降到 1.0，r ≥ 20 处 0.8）。</summary>
    internal static ThicknessField SynthDisc(double s, double phx, double phz, double rd, double R, double leg)
    {
        double half = R + 3;
        double x0 = -half + phx, z0 = -half + phz;
        int nx = (int)Math.Ceiling(2 * half / s) + 2, nz = nx;
        var f = new ThicknessField { X0 = x0, Z0 = z0, Step = s, Nx = nx, Nz = nz, T = new double[nx * nz] };
        for (int i = 0; i < nx; i++) for (int j = 0; j < nz; j++)
        {
            double x = x0 + i * s, z = z0 + j * s, r = Math.Sqrt(x * x + z * z);
            double t = 0;
            if (r >= rd && r <= R) t = r < rd + leg ? 1.6 - 0.6 * (r - rd) / leg : (r < 20 ? 1.0 : 0.8);
            f.T[i * nz + j] = t;
        }
        f.XMinMaterial = -R; f.XMaxMaterial = R; f.ZMinMaterial = -R; f.ZMaxMaterial = R;
        return f;
    }

    /// <summary>测试侧另写的连通分量（不调 FlangeMesher.MeshConnectivity）：长度 &gt; 0 的内部面相连；锚 = 管孔边界面所在格 ∪ ShellMesh.ClampSetCells()。</summary>
    static (int Comps, int Floating, double FloatingArea, int[] Sizes) IndependentComponents(ShellMesh m)
    {
        int n = m.CellCount;
        var lab = Enumerable.Repeat(-1, n).ToArray();
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (var fc in m.Faces) if (fc.B >= 0 && fc.Length > 0) { adj[fc.A].Add(fc.B); adj[fc.B].Add(fc.A); }
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            if (lab[i] >= 0) continue;
            var st = new Stack<int>(); st.Push(i); lab[i] = k;
            while (st.Count > 0) { int c = st.Pop(); foreach (int q in adj[c]) if (lab[q] < 0) { lab[q] = k; st.Push(q); } }
            k++;
        }
        var anchor = new bool[k];
        foreach (var fc in m.Faces) if (fc.B < 0 && fc.Tag == ShellMesh.TagHole) anchor[lab[fc.A]] = true;
        var cl = m.ClampSetCells();
        for (int i = 0; i < n; i++) if (cl[i]) anchor[lab[i]] = true;
        double fa = 0; for (int i = 0; i < n; i++) if (!anchor[lab[i]]) fa += m.Area[i];
        var sizes = Enumerable.Range(0, n).GroupBy(i => lab[i]).Select(g => g.Count()).OrderByDescending(c => c).ToArray();
        return (k, anchor.Count(a => !a), fa, sizes);
    }

    [Fact]
    public void 门f_失配切出的浮空料块必须量得并报出_合成盘()
    {
        const double rh = 12.9, s = 0.5;
        int floatingOnCases = 0;
        foreach (double d in new[] { 0.0, -0.75, 1.0 })
        {
            var tf = SynthDisc(s, 0.13, 0.07, rh + d, 25, 1.2);
            var mOn = FlangeMesher.BuildFromField(tf, rh, 0, 1.0, 4.0, 20, 2.0, 0.25, 16, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
            var mOff = FlangeMesher.BuildFromMaterialWith(tf, rh, Off, 0, 1.0, 4.0, 20, 2.0, 0.25, 16, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
            foreach (var (branch, m) in new[] { ("修后", mOn), ("改回", mOff) })
            {
                var ind = IndependentComponents(m);
                var rc = m.Recipe!;
                var notes = LineRunner.HoleArcDrawingNotes(m, tf, rh);
                bool noteHas = notes.Any(n => n.Contains("与管孔面、压接格都不相连", StringComparison.Ordinal));
                _o.WriteLine($"合成盘 图纸孔 rh {d:+0.00;-0.00} mm {branch}：{m.CellCount} 格；配方 分量 {rc.MeshComponents}、浮空 {rc.FloatingComponents}（{rc.FloatingAreaMm2:0.0000} mm²）"
                           + $"｜测试侧 分量 {ind.Comps}（大小 {string.Join(",", ind.Sizes.Take(6))}）、浮空 {ind.Floating}（{ind.FloatingArea:0.0000} mm²）｜整线说明带浮空句 {(noteHas ? "是" : "否")}");
                Assert.Equal(ind.Comps, rc.MeshComponents);
                Assert.Equal(ind.Floating, rc.FloatingComponents);
                Assert.Equal(ind.FloatingArea, rc.FloatingAreaMm2, 9);
                if (rc.FloatingComponents > 0)
                {
                    Assert.True(noteHas, $"rh {d:+0.00;-0.00} {branch}：浮空 {rc.FloatingComponents} 块，整线说明没报");
                    Assert.Contains("⚠ 其中", rc.Describe());
                    if (branch == "修后") floatingOnCases++;
                }
                else
                {
                    Assert.False(noteHas);
                    Assert.Contains("没有浮空料块", rc.Describe());
                }
            }
        }
        Assert.True(floatingOnCases > 0, "三例修后都没有浮空料块 —— 门 f 空守（审查实测 −0.75、+1.0 修后都有；规则若按决 98 改了，这里要照实改写）");
    }

    // ───────────────────────────── 归因（慢，只印不判）─────────────────────────────
    /// <summary>带内料量：与带（|r − rh| &lt; b）相交的栅格方格上 f.Integrate 之和（同一组方格量改回、修后、解析板三份，差就是带里料形的差）。</summary>
    internal static (double A, double V) BandTiles(ThicknessField tf, IMaterialField f, double rh, double b)
    {
        double s = tf.Step, h = 0.5 * s, A = 0, V = 0;
        for (int i = 0; i < tf.Nx; i++)
            for (int j = 0; j < tf.Nz; j++)
            {
                double cx = tf.X0 + i * s, cz = tf.Z0 + j * s;
                double x0 = cx - h, x1 = cx + h, z0 = cz - h, z1 = cz + h;
                double nx = Math.Clamp(0, x0, x1), nz = Math.Clamp(0, z0, z1);
                double fx = Math.Max(Math.Abs(x0), Math.Abs(x1)), fz = Math.Max(Math.Abs(z0), Math.Abs(z1));
                if (!(Math.Sqrt(nx * nx + nz * nz) < rh + b && Math.Sqrt(fx * fx + fz * fz) > rh - b)) continue;
                var I = f.Integrate(x0, x1, z0, z1);
                A += I.Area; V += I.Volume;
            }
        return (A, V);
    }

    static int ArcFaces(ShellMesh m) => m.Faces.Count(fc => fc.B < 0 && !double.IsNaN(fc.ArcRadiusMm));
    /// <summary>2026-09-23 审查 M1 后加：配方里的连通分量观测量，印成「分量（浮空块，浮空面积）」。</summary>
    static string Conn(ShellMesh m) => $"{m.Recipe!.MeshComponents}（{m.Recipe.FloatingComponents}，{m.Recipe.FloatingAreaMm2:0.000}）";

    /// <summary>
    /// 「开 − 关」归因（决 101 A；组 C 共同做法：每条改动出一份开减关归因）：两例 × 栅格步 1.0／0.5／0.25／0.1（相位 0 = 门的建法）逐项并列改回／修后（另印带宽 s/√2）：
    /// 抽热差（图纸 − 生产 Build）、发热差、带内料面积与体积（与带相交的栅格方格上积分；另印解析板同一组方格）、等效孔半径（§0.-20 HoleRadiusOf 读原始栅格 ⇒ 两支按构造相同；
    /// 另按带内料面积差折成 √(rh² − ΔA/π)）、孔弧覆盖率与缺口段数、孔面条数（弧面）、格边孔圆内侧有料长度。
    /// 代价：人为把图纸孔径改大（+0.3 mm 小于导航档一格 0.5、大于判决档一格 0.25；+1.0 大于两档），W08 默认几何（F3 失配探针同一几何）量 G_in（1214 A 等温电流场的 CurrentInA，F3 A1(6) 同一口径），
    /// 以及两例在步 1.0／0.5 上 +0.3 mm 的抽热。只印不判（失配时参照本身不适定，F3 A1(6)）。
    /// </summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 归因_开减关_两例四档_与孔径失配吸收代价()
    {
        var sb = new StringBuilder();
        string file = DeliverableOut.Stamped("R48_孔环判料_开减关归因_2026-09-23.txt");
        void W(string t) { sb.AppendLine(t); _o.WriteLine(t); File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); }
        W($"决 101 A「开 − 关」归因（改回 = MeshRules.HoleBandCircle = false；修后 = 生产入口 BuildFromField；√2 = 带宽 s/√2）　开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W("列：步｜抽热差 W（图纸 − 生产 Build）改回／修后／√2｜发热差 %｜带内料面积 mm² 改回／修后／解析板（与 |r − rh| < s 相交的栅格方格）｜带内料体积 mm³ 同｜等效孔半径 mm：HoleRadiusOf(原始栅格)，按带内面积差折算 改回／修后｜覆盖率（缺口段）改回／修后｜弧面条数 改回／修后｜格边孔圆内侧有料 mm 改回／修后｜格数 改回／修后");
        foreach (int which in new[] { 1, 0 })
        {
            var (g, lc, iA, tRoot, tSet, clampC, tabInsul, name) = Case(which);
            double rh = g.HoleRadiusMm;
            var mA = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
            var a = DrawingPathParityTests.SolveOne(mA, lc, g, iA, tRoot, tSet, clampC, tabInsul);
            var an = new AnalyticMaterial(g);
            W($"══ {name}（rh {rh}）：生产 Build 发热 {a.QGen:0.000} W 抽热 {a.QFromTube:0.0000} W，{a.Cells} 格，弧面 {ArcFaces(mA)} 条");
            foreach (double st in new[] { 1.0, 0.5, 0.25, 0.1 })
            {
                var tf = FlangeMesher.Rasterize(g, st, 2.0);
                var mOff = Mesh(tf, g, lc, Off); var mOn = Mesh(tf, g, lc, null); var mR2 = Mesh(tf, g, lc, BandSqrt2);
                var sOff = DrawingPathParityTests.SolveOne(mOff, lc, g, iA, tRoot, tSet, clampC, tabInsul);
                var sOn = DrawingPathParityTests.SolveOne(mOn, lc, g, iA, tRoot, tSet, clampC, tabInsul);
                var sR2 = DrawingPathParityTests.SolveOne(mR2, lc, g, iA, tRoot, tSet, clampC, tabInsul);
                var bOff = BandTiles(tf, tf, rh, st); var bOn = BandTiles(tf, mOn.Material!, rh, st); var bAn = BandTiles(tf, an, rh, st);
                double rEq(double dA) => Math.Sqrt(rh * rh - dA / Math.PI);
                W($"  步 {st}｜抽热差 {sOff.QFromTube - a.QFromTube:+0.0000;-0.0000}／{sOn.QFromTube - a.QFromTube:+0.0000;-0.0000}／{sR2.QFromTube - a.QFromTube:+0.0000;-0.0000}"
                  + $"｜发热差 {(sOff.QGen - a.QGen) / a.QGen * 100:+0.0000;-0.0000}／{(sOn.QGen - a.QGen) / a.QGen * 100:+0.0000;-0.0000}／{(sR2.QGen - a.QGen) / a.QGen * 100:+0.0000;-0.0000}"
                  + $"｜带内料面积 {bOff.A:0.0000}／{bOn.A:0.0000}／{bAn.A:0.0000}（改回 − 解析 {bOff.A - bAn.A:+0.0000;-0.0000}，修后 − 解析 {bOn.A - bAn.A:+0.0000E+0;-0.0000E+0}）"
                  + $"｜带内料体积 {bOff.V:0.0000}／{bOn.V:0.0000}／{bAn.V:0.0000}"
                  + $"｜等效孔半径 HoleRadiusOf {PlateShapeAnalyzer.HoleRadiusOf(tf):0.0000}，折算 {rEq(bOff.A - bAn.A):0.0000}／{rEq(bOn.A - bAn.A):0.0000}"
                  + $"｜覆盖率 {mOff.HoleArcCoverage:0.000000}（{mOff.HoleArcGapCount}）／{mOn.HoleArcCoverage:0.000000}（{mOn.HoleArcGapCount}）"
                  + $"｜弧面 {ArcFaces(mOff)}／{ArcFaces(mOn)}｜格边孔圆内侧有料 {FlangeMesher.HoleInsideMaxMm(mOff, mOff.Material!, rh):0.0000}／{FlangeMesher.HoleInsideMaxMm(mOn, mOn.Material!, rh):0.0000}"
                  + $"｜格 {sOff.Cells}／{sOn.Cells}");
            }
        }

        W("");
        W("── 代价：图纸孔径人为改大，看带内按解析圆判料吸收多少（只印不判）");
        W("  (i) W08 默认几何（F3 失配探针 MISMATCH 同一几何，R48ArcGapDiagTests.W08R31(r31: false) 同一建法），G_in = ShellCurrent.SolveFor(1214 A, ρ(控温)).CurrentInA");
        foreach (double fine in new[] { 0.0, 1.0 })
        {
            double gAn = double.NaN;
            foreach (double dHole in new[] { 0.0, 0.3, 1.0 })
            {
                var p = new DesignInputs();
                var d = R48NMeshGateTests.Design("W08");
                var (lc, g, mA, _) = R48NMeshGateTests.Build(d, p, fine);
                double rh = g.HoleRadiusMm, tS = lc.SetpointC[0];
                double hFinest = lc.MeshInnerMm > 1e-9 ? Math.Min(lc.MeshFineMm, lc.MeshInnerMm) : lc.MeshFineMm;
                double step = Math.Min(lc.ThicknessStepMm, FlangeMesher.RasterStepForFile(hFinest));   // LineRunner 图纸路径取栅格步的同一式
                double G(ShellMesh m) => ShellCurrent.SolveFor(lc, m, 1214, Materials.PtResistivity(tS) * 1e3, tS).CurrentInA;
                if (double.IsNaN(gAn)) gAn = G(mA);
                g.HoleRadiusMm = rh + dHole; var tf = FlangeMesher.Rasterize(g, step); g.HoleRadiusMm = rh;
                var mOff = FlangeMesher.BuildFromMaterialWith(tf, rh, Off, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
                var mOn = FlangeMesher.BuildFromField(tf, rh, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
                double gOff = G(mOff), gOn = G(mOn);
                bool noteOff = LineRunner.HoleArcDrawingNotes(mOff, tf, rh).Any(n => n.Contains("图纸孔半径", StringComparison.Ordinal));
                bool noteOn = LineRunner.HoleArcDrawingNotes(mOn, tf, rh).Any(n => n.Contains("图纸孔半径", StringComparison.Ordinal));
                W($"    W08 默认 {(fine == 0 ? "导航" : "判决")}档 栅格步 {step}，图纸孔半径 = rh + {dHole}：解析 G_in {gAn:0.000000}｜改回 {gOff:0.000000}（{(gOff / gAn - 1) * 100:+0.000;-0.000} %，覆盖率 {mOff.HoleArcCoverage:0.0000}／合计缺 {(1 - mOff.HoleArcCoverage) * 2 * Math.PI * rh:0.000} mm）"
                  + $"｜修后 {gOn:0.000000}（{(gOn / gAn - 1) * 100:+0.000;-0.000} %，覆盖率 {mOn.HoleArcCoverage:0.0000}／合计缺 {(1 - mOn.HoleArcCoverage) * 2 * Math.PI * rh:0.000} mm）"
                  + $"｜HoleRadiusOf {PlateShapeAnalyzer.HoleRadiusOf(tf):0.000}，失配句 改回 {(noteOff ? "有" : "无")}／修后 {(noteOn ? "有" : "无")}"
                  + $"｜连通分量（浮空块，浮空面积 mm²）改回 {Conn(mOff)}／修后 {Conn(mOn)}");   // 2026-09-23 审查 M1 后加印（只印）
            }
        }
        W("  (ii) 两例、相位 0，图纸孔半径 = rh + 0.3（小于步 1.0／0.5 的一格）：抽热差（图纸 − 生产 Build(rh)）");
        foreach (int which in new[] { 1, 0 })
        {
            var (g, lc, iA, tRoot, tSet, clampC, tabInsul, name) = Case(which);
            double rh = g.HoleRadiusMm;
            var mA = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
            var a = DrawingPathParityTests.SolveOne(mA, lc, g, iA, tRoot, tSet, clampC, tabInsul);
            foreach (double st in new[] { 1.0, 0.5 })
            foreach (double dHole in new[] { 0.0, 0.3 })
            {
                g.HoleRadiusMm = rh + dHole; var tf = FlangeMesher.Rasterize(g, st, 2.0); g.HoleRadiusMm = rh;
                var mOff = Mesh(tf, g, lc, Off); var mOn = Mesh(tf, g, lc, null);
                var sOff = DrawingPathParityTests.SolveOne(mOff, lc, g, iA, tRoot, tSet, clampC, tabInsul);
                var sOn = DrawingPathParityTests.SolveOne(mOn, lc, g, iA, tRoot, tSet, clampC, tabInsul);
                W($"    {name} 步 {st} 图纸孔 rh + {dHole}：抽热差 改回 {sOff.QFromTube - a.QFromTube:+0.0000;-0.0000}（覆盖率 {mOff.HoleArcCoverage:0.0000}）／修后 {sOn.QFromTube - a.QFromTube:+0.0000;-0.0000}（覆盖率 {mOn.HoleArcCoverage:0.0000}）；"
                  + $"发热差 改回 {(sOff.QGen - a.QGen) / a.QGen * 100:+0.000;-0.000} %／修后 {(sOn.QGen - a.QGen) / a.QGen * 100:+0.000;-0.000} %；HoleRadiusOf {PlateShapeAnalyzer.HoleRadiusOf(tf):0.000}"
                  + $"；连通分量（浮空块，浮空面积 mm²）改回 {Conn(mOff)}／修后 {Conn(mOn)}");   // 2026-09-23 审查 M1 后加印（只印）
            }
        }
        W("");
        // 2026-09-23 审查后改（T4、PC-3）：机器核数与负载改为运行时量（原先把「4 核、load 30～40」写死在标题里）；组合改为生产组合 (h, s = min(ThicknessStepMm, RasterStepForFile(h)))
        //   —— 原先固定 h = 2 换栅格步 1.0／0.5／0.1，(2, 1.0)、(2, 0.1) 都不是生产组合。只印不判。
        static string Load() { try { return File.Exists("/proc/loadavg") ? File.ReadAllText("/proc/loadavg").Split(' ')[0] : "未量"; } catch { return "未量"; } }
        string load0 = Load();
        W($"── 成本：建一张图纸网格的墙钟（改回／修后，各建 3 次取最短；生产组合 (h, s)，内带 0、整张网格按 h ÷ MeshFineMm 缩放；本机 {Environment.ProcessorCount} 核，成本段开始时 load {load0}（/proc/loadavg 1 分钟值）；耗时受同机负载影响，只作量级）");
        foreach (int which in new[] { 1, 0 })
        {
            var (g, lc, _, _, _, _, _, name) = Case(which);
            foreach (double h in new[] { 2.0, 1.0, 0.5, 0.25, 0.2 })
            {
                double st = Math.Min(lc.ThicknessStepMm, FlangeMesher.RasterStepForFile(h));   // LineRunner 图纸路径取栅格步的同一式
                var tf = FlangeMesher.Rasterize(g, st, 2.0);
                double scale = h / lc.MeshFineMm;
                double Best(MeshRules? rr)
                {
                    double best = double.PositiveInfinity;
                    for (int k = 0; k < 3; k++) { var sw = System.Diagnostics.Stopwatch.StartNew(); Mesh(tf, g, lc, rr, scale); sw.Stop(); best = Math.Min(best, sw.Elapsed.TotalMilliseconds); }
                    return best;
                }
                double tOff = Best(Off), tOn = Best(null);
                W($"    {name} h {h}／栅格步 {st}：改回 {tOff:0.0} ms／修后 {tOn:0.0} ms（+{tOn - tOff:0.0} ms）");
            }
        }
        W($"  成本段结束时 load {Load()}");
        W($"证据：{file}");
    }
}
