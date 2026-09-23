using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★ R48 G2 快门（2026-09-15，Opus 5 写；依据常驻物理把关人第十一轮；复审同日重写）：**升温两节点补铜排通道与舌片保温，法兰节点四项在稳态场上逐项标定**。
///
/// ══ 改了什么（声明在 RampTwoNode 类注释、RampTwoNode.CalibrateNode、LineRunner.FlangeLumped 的注释里，本门只调生产函数）
///   · 一个节点口径：节点格 = 场里不被边界钉住的格（ShellThermalResult.BoundaryCell），节点温度 = 这些格的体积加权均温；
///   · 发热：参考电阻 = 节点格焦耳热 ÷ 场电流²，参考温度 = 节点温度（第一版挂管根温度 —— 审查意见 blocker）；
///   · 表面散热：配方（圆盘按本片圆盘保温、舌片区按本片舌保温，DesignScreen.PlateFluxWPerM2）× κ，κ 在节点温度上还原节点格场散热；
///   · 流进铜排：G_夹 = 流进铜排 ÷（节点温度 − 夹持参考温度），升温期夹持随法兰按比例升；
///   · 管孔：孔侧导度 = 管孔流入 ÷（管根温度 − 节点温度），与管壁翅片导度串联（第一版按孔壁几何式 —— 审查意见 blocker）；
///   · 整片热稳定的夹持导度 = G_夹·(1 − r)（与升温同一个夹持假设），舌片几何导度的裕度另算一份当灵敏度对照；
///   · 判据表「升温期法兰−管峰值」在物理把关人复核前判不了、不给数。
///
/// ══ 五道门（跑前写死的判读）
///   一、积分跑到两节点稳态：合成输入恒流跑到稳态，法兰节点「发热 − 表面 − 铜排 + 管来」与管节点「发热 − 散热 − 管来」都 ≤ 发热的 0.5 %；
///       ⚠ 这是**恒等式**（热容吸收的定义就是这四项之和，跑到稳态自然趋 0），只验积分到位、通道不空转，**验不了集总模型对不对** —— 那是门五的事（审查意见 major）。
///   二、舌片保温确实进了散热与热容：舌片区散热 = 2 × 面积 × PlateFluxWPerM2(本片舌保温)（查表插值 ≤ 1 %）；5 mm 舌保温在 700／1000 °C 低于裸铂；0 mm 与 NaN 逐位相同；
///       舌保温热容与厚度成正比、与圆盘保温热容同一公式（面积、厚度相同时相等），法兰热容多出的正是它（调 Model 的公开量，不手抄 ρc 配方）；
///       给了表面标定点 ⇒ 标定温度上表面散热 = 标定值、别的温度上 = κ × 配方，舌保温仍在（κ 不改舌／裸之比）。
///   三、标定本体（合成网格与合成逐格账）：节点格、均温、四项、残差、排除与不排除两种口径的残差相同、热导／自由端、标定不了的各种情形（铜排项成而管孔项不成时整片热稳定仍判得了）；
///       模型在标定点上逐项还原合成场的节点账；整片热稳定给 NaN 判不了、给值就用、几何导度总算一份且不随给的值变。
///   四甲、整线（设计记录 W08 只跑一段、两片舌厚 1.26 mm，舌保温用设计记录自己的 0.4／0.3 mm）：标定取自本片场、整片热稳定与升温同一份（热稳定取 G·(1 − r)）、
///       舌保温取本片热解那一份、网格取本片场、判据表升温那一行判不了不给数、附注说清原因、热稳定附注写割线与几何导度裕度。
///   四乙、整线（W08 一段原样，两片热从法兰流进管、节点均温低于管根）：**记录模型缺陷，不是成立条件**（R48 G2 复审二 2026-09-15 Opus 5，审查意见 major 改写）——
///       管孔流向与温差反号时单点割线标定不了，是两节点模型表示不了这种热流分布（壳解沿升温轨迹证伪了孔侧常数导度，见 RampTwoNode 类注释复审二那段），
///       本门守的是**缺陷看得见、不被数字盖住**：升温两节点不解、判据表那一行暂不给数并写算不出来的原因与「模型的缺陷」；铜排项成了 ⇒ 整片热稳定照样判得了。
///       「本算例标定不了」只作前提断言（门量得到这条路），将来模型改了让它标定得了，要换算例，不是改判读。
///       （算例怎么选的见 W08OneSeg 上方注释：复审首跑发现原样算例标定不了，改作这条路的门。）
///   五、场对账（审查意见 major「能量守恒门是恒等式」的补门；同一次整线解）：在标定点（法兰 = 节点温度、电流 = 场电流、管节点 = 管根 + 管孔流入 ÷ 翅片导度）调 RampTwoNode.Model.Flows，
///       发热 ↔ 节点格场发热、表面 ↔ 节点格场散热、铜排 ↔ FlangeOut.QClampW、管来 ↔ FlangeOut.QFromTubeW 逐项比；热容吸收 ↔ 场的能量残差。
///       容差（跑前写死，不看数据）：前三项相对 1e-9（同一温度上同一张表、同一个电流，只剩浮点）；管来相对 1e-6（翅片温降迭代停在 1e-9 K）；
///       热容吸收与场残差之差 ≤ 1e-6 × 发热（求和顺序不同的浮点）；场残差本身 ≤ 节点格数 × ShellThermal.ResidualRelTol × 发热（场收敛判据逐格 |r| ≤ 容差 × 总焦耳热，残差是逐格 r 之和）。
///       节点格 = 场的边界格取反，而且 = 压接格取反（定温边界的生产网格上两者一致，老口径回归靠它）。
///       若参考温度仍挂管根温度，发热一项差 ρ(均温)/ρ(管根) 量级（约 10 %），本门当场红。
///       ⚠ R48 G2 复审二（2026-09-15 Opus 5）：本门只证「在标定点上」对得上，**证不了**离开标定点的轨迹（复审探针表一：轨迹上比第一版偏得更远）。
///   六、界面附注与带号数（R48 G2 复审二 2026-09-15 Opus 5，审查意见 minor 三条）：负数取整成零不印「-+0.0」（SizerResult.Signed，net8 实测病例）；
///       整片热稳定的夹持导度标定不出来而判不了时，附注写原因（LineRunner.FlangeStabNote 追加标定的 Note）；追不追加夹持导度那一段读 ClampFromField 位；附注不写「割线」；
///       管孔标定不成的原因说明是两节点模型的缺陷。
///   ── 复审二还改了（门三、四甲、四乙里）：出处断言改读 FlangeStability.Result.ClampFromField（不比文字）；门三的几何导度裕度改比两次 Check 的 Margin（不手抄公式）；
///       四甲／四乙断言升温那一行 ConstraintOut.Withheld、界面附注不写「孔边」而写「不代表片上最热处」、安装报告那一行印「参考（暂不给数）」。
/// </summary>
public class R48G2RampClampChannelGateTests
{
    private readonly ITestOutputHelper _out;
    public R48G2RampClampChannelGateTests(ITestOutputHelper o) { _out = o; }

    private static RampTwoNode.Inputs Synthetic(double iSeg, double gClamp, double tabInsulMm) => new()
    {
        WallMm = 1.0, FlangeMassG = 340, FlangeAreaInsulMm2 = 730, FlangeAreaBareMm2 = 4580,
        FlangeResistanceRefOhm = 1.7e-4, FlangeRefTempC = 1090, HoleRadiusMm = 26, PlateEqOuterRadiusMm = 45, FlangeThickMm = 1.2,
        SharedFactor = 1.0, DesignCurrentA = iSeg, FromC = 25, TargetC = 1700, MaxHours = 40,
        Mode = RampControl.ConstantCurrent,
        TabInsulThickMm = tabInsulMm, ClampConductanceWPerK = gClamp, ClampFollowRatio = 0.45, ClampColdEndC = 25,
    };

    [Fact]
    public void 一_积分跑到两节点稳态_恒等式只验积分到位()
    {
        var p = new DesignInputs();
        double iHold = RampTwoNode.QuasiStaticCurrentA(p, 1.0, 900.0, 0.0);
        var on = RampTwoNode.Solve(p, Synthetic(iHold, 0.6, 5.0));
        var off = RampTwoNode.Solve(p, Synthetic(iHold, 0.0, 5.0));
        foreach (var (name, r) in new[] { ("有通道", on), ("没通道", off) })
        {
            var e = r.AtEnd;
            _out.WriteLine($"{name}：{e.TimeS / 3600:0.00} h　管 {e.TTubeC:0.00} °C　法兰 {e.TFlangeC:0.00} °C　电流 {e.CurrentA:0.0} A　"
                         + $"法兰 发热 {e.GenW:0.000} = 表面 {e.SurfaceW:0.000}（圆盘 {e.SurfaceDiscW:0.000} + 舌 {e.SurfaceTabW:0.000}）+ 铜排 {e.ClampW:0.000}（夹持 {e.ClampTempC:0.0} °C）− 管来 {e.FromTubeW:0.000} + 热容 {e.StorageW:0.0000}　"
                         + $"管 发热 {e.TubeGenW:0.000} = 散热 {e.TubeLossW:0.000} + 管来 {e.FromTubeW:0.000} + 热容 {e.TubeStorageW:0.0000}　注：{r.Note}");
            Assert.False(r.FlangeMelts, name + "：法兰熔了，合成输入没选好");
            Assert.False(r.TubeReached, name + "：管到了目标温度就停了，没跑到稳态");
            Assert.True(Math.Abs(e.GenW - e.SurfaceW - e.ClampW + e.FromTubeW) <= 0.005 * e.GenW,
                $"{name}：法兰节点没跑到稳态（发热 {e.GenW} 表面 {e.SurfaceW} 铜排 {e.ClampW} 管来 {e.FromTubeW}）");
            Assert.True(Math.Abs(e.TubeGenW - e.TubeLossW - e.FromTubeW) <= 0.005 * e.TubeGenW,
                $"{name}：管节点没跑到稳态（发热 {e.TubeGenW} 散热 {e.TubeLossW} 管来 {e.FromTubeW}）");
            Assert.Equal(e.SurfaceDiscW + e.SurfaceTabW, e.SurfaceW, 9);
        }
        Assert.True(on.AtEnd.ClampW > 50, "铜排通道空转");
        Assert.Equal(0.0, off.AtEnd.ClampW);
        Assert.True(double.IsNaN(off.AtEnd.ClampTempC));
        Assert.Equal(25 + 0.45 * (on.AtEnd.TFlangeC - 25), on.AtEnd.ClampTempC, 9);
        Assert.Equal(0.6 * (on.AtEnd.TFlangeC - on.AtEnd.ClampTempC), on.AtEnd.ClampW, 9);
        Assert.True(off.AtEnd.TFlangeC > on.AtEnd.TFlangeC + 20, $"关掉铜排通道法兰终温应更高：{off.AtEnd.TFlangeC} vs {on.AtEnd.TFlangeC}");
        // 结果原样带出输入的各项；老调用点的默认值 = 没有通道、舌片裸铂、表面不标定、孔侧按几何式
        Assert.Equal(0.6, on.ClampConductanceWPerK); Assert.Equal(0.45, on.ClampFollowRatio); Assert.Equal(5.0, on.TabInsulThickMm);
        Assert.Equal(1.0, on.SurfaceScale); Assert.True(double.IsNaN(on.HolePlateConductanceWPerK));
        var d = new RampTwoNode.Inputs();
        Assert.Equal(0.0, d.ClampConductanceWPerK);
        Assert.True(double.IsNaN(d.TabInsulThickMm) && double.IsNaN(d.SurfaceCalibLossW) && double.IsNaN(d.SurfaceCalibTempC) && double.IsNaN(d.HolePlateConductanceWPerK));
    }

    [Fact]
    public void 二_舌片保温确实进了散热与热容_表面标定只乘系数()
    {
        var p = new DesignInputs { FlangeInsulThickMm = 5.0 };
        var mBare = new RampTwoNode.Model(p, Synthetic(1000, 0, double.NaN));
        var mZero = new RampTwoNode.Model(p, Synthetic(1000, 0, 0.0));
        var mIns = new RampTwoNode.Model(p, Synthetic(1000, 0, 5.0));
        var mIns10 = new RampTwoNode.Model(p, Synthetic(1000, 0, 10.0));
        const double aTab = 4580, aDisc = 730;
        foreach (double t in new[] { 700.0, 1000.0 })
        {
            double wantIns = 2e-6 * aTab * DesignScreen.PlateFluxWPerM2(p, t, 5.0);
            double wantBare = 2e-6 * aTab * DesignScreen.PlateFluxWPerM2(p, t, 0.0);
            _out.WriteLine($"{t} °C：舌片区散热 裸 {mBare.FlangeSurfaceTabW(t):0.000} W（配方 {wantBare:0.000}）　5 mm {mIns.FlangeSurfaceTabW(t):0.000} W（配方 {wantIns:0.000}）　圆盘 {mBare.FlangeSurfaceDiscW(t):0.000} / {mIns.FlangeSurfaceDiscW(t):0.000} W");
            Assert.True(Math.Abs(mIns.FlangeSurfaceTabW(t) / wantIns - 1) <= 0.01, $"舌片区（5 mm）散热与唯一配方差 {mIns.FlangeSurfaceTabW(t) / wantIns - 1:P3}");
            Assert.True(Math.Abs(mBare.FlangeSurfaceTabW(t) / wantBare - 1) <= 0.01, $"舌片区（裸）散热与唯一配方差 {mBare.FlangeSurfaceTabW(t) / wantBare - 1:P3}");
            Assert.True(mIns.FlangeSurfaceTabW(t) < mBare.FlangeSurfaceTabW(t), "5 mm 舌保温没把舌片区散热压下去");
            Assert.Equal(mBare.FlangeSurfaceTabW(t), mZero.FlangeSurfaceTabW(t));      // 0 mm = 裸铂，逐位
            Assert.Equal(mBare.FlangeSurfaceDiscW(t), mIns.FlangeSurfaceDiscW(t));     // 圆盘面不受舌保温影响
            // 舌保温热容计入法兰节点：多出来的正是 Model 报的舌保温热容（审查意见 minor：原先门里手抄了 2×面积×厚×ρc×0.5）
            Assert.Equal(mIns.CapInsulTabJPerK, mIns.CapFlange(t) - mBare.CapFlange(t), 9);
        }
        Assert.Equal(0.0, mBare.CapInsulTabJPerK); Assert.Equal(0.0, mZero.CapInsulTabJPerK);
        Assert.True(mIns.CapInsulTabJPerK > 0);
        Assert.Equal(2.0, mIns10.CapInsulTabJPerK / mIns.CapInsulTabJPerK, 12);          // 与厚度成正比
        // 与圆盘保温同一公式：舌片区与圆盘面积、厚度都相同时两份热容相等
        var gSame = Synthetic(1000, 0, 5.0); gSame.FlangeAreaBareMm2 = aDisc;
        var mSame = new RampTwoNode.Model(p, gSame);
        Assert.True(mSame.CapInsulDiscJPerK > 0);
        Assert.Equal(mSame.CapInsulDiscJPerK, mSame.CapInsulTabJPerK, 12);

        // 表面标定：标定温度上表面散热 = 标定值；别的温度上 = κ × 配方；κ 不改舌／裸之比（舌保温仍在）
        var gCal = Synthetic(1000, 0, 5.0); gCal.SurfaceCalibLossW = 80.0; gCal.SurfaceCalibTempC = 900.0;
        var mCal = new RampTwoNode.Model(p, gCal);
        var gCalBare = Synthetic(1000, 0, double.NaN); gCalBare.SurfaceCalibLossW = 80.0; gCalBare.SurfaceCalibTempC = 900.0;
        var mCalBare = new RampTwoNode.Model(p, gCalBare);
        _out.WriteLine($"表面标定：κ {mCal.SurfaceScale:0.0000}（舌 5 mm）、{mCalBare.SurfaceScale:0.0000}（舌裸）；900 °C 散热 {mCal.FlangeSurfaceW(900):0.000} W");
        Assert.Equal(80.0, mCal.FlangeSurfaceW(900.0), 9);
        Assert.Equal(mCal.SurfaceScale * mIns.FlangeSurfaceRecipeW(700.0), mCal.FlangeSurfaceW(700.0), 9);
        Assert.Equal(mIns.FlangeSurfaceRecipeW(700.0), mIns.FlangeSurfaceW(700.0));   // 没标定 κ = 1，逐位
        Assert.Equal(mIns.FlangeSurfaceTabW(700.0) / mBare.FlangeSurfaceTabW(700.0),
                     mCal.FlangeSurfaceTabW(700.0) / mCalBare.FlangeSurfaceTabW(700.0) * (mCalBare.SurfaceScale / mCal.SurfaceScale), 9);
        var gBad = Synthetic(1000, 0, 5.0); gBad.SurfaceCalibLossW = -1.0; gBad.SurfaceCalibTempC = 900.0;
        Assert.True(double.IsNaN(new RampTwoNode.Model(p, gBad).SurfaceScale), "标定值非正时系数应为 NaN（调用方据此判标定不了）");
    }

    [Fact]
    public void 三_标定本体_合成网格与合成逐格账()
    {
        var plate = new FlangePlate { HoleRadiusMm = 26.0 };
        var mesh = FlangeMesher.Build(plate, 0, 4.0, 11.0, 50.0, 40.0, clampBandMm: double.NaN, clampFullFace: true);   // 生产配方，只验标定口径
        int n = mesh.CellCount;
        Assert.True(mesh.ClampCell.Length == n && mesh.ClampCell.Any(b => b), "合成网格没有压接格，门空转");
        var boundary = mesh.ClampCell.ToArray();
        var tTwo = Enumerable.Range(0, n).Select(k => boundary[k] ? 450.0 : mesh.Centroid[k].X >= 0 ? 1100.0 : 900.0).ToArray();
        var gen = Enumerable.Range(0, n).Select(k => 0.02 * mesh.Area[k] * (boundary[k] ? 0.5 : 1.0)).ToArray();
        var loss = Enumerable.Range(0, n).Select(k => 0.01 * mesh.Area[k]).ToArray();
        var pFix = new DesignInputs { BusbarClampTempC = 450, BusbarConductanceWPerK = -1, BusbarSinkTempC = 25 };
        const double qClamp = 200.0, qHole = 15.0, tRoot = 1150.0, iA = 1700.0;

        // 期望值（门里的求和是本门的判读，不是生产配方）
        double v = 0, vt = 0, sg = 0, sl = 0, gB = 0, lB = 0; int cells = 0;
        for (int k = 0; k < n; k++)
        {
            if (boundary[k]) { gB += gen[k]; lB += loss[k]; continue; }
            double w = mesh.Area[k] * mesh.Thickness[k];
            v += w; vt += w * tTwo[k]; sg += gen[k]; sl += loss[k]; cells++;
        }
        double tNode = vt / v;

        var k1 = RampTwoNode.CalibrateNode(mesh, tTwo, gen, loss, boundary, qClamp, qHole, tRoot, iA, pFix, excludeBoundaryCells: true);
        _out.WriteLine($"定温、排除：{k1.Note}　节点 {k1.NodeCells} 格 {k1.NodeVolumeMm3:0.0} mm³　G {k1.GEffWPerK:0.0000}　r {k1.FollowRatio:0.0000}　R {k1.ResistanceRefOhm:E4}　孔侧 {k1.HolePlateGWPerK:0.0000}　残差 {k1.ResidualW:0.000}");
        Assert.True(k1.Ok && k1.ClampOk, k1.Note);
        Assert.Equal(ShellThermal.ClampBoundary.FixedTemp, k1.Mode);
        Assert.True(k1.BoundaryCellsExcluded);
        Assert.Equal(cells, k1.NodeCells);
        Assert.Equal(Enumerable.Range(0, n).Select(k => !boundary[k]).ToArray(), k1.NodeCell);
        Assert.Equal(v, k1.NodeVolumeMm3, 9);
        Assert.Equal(tNode, k1.TNodeC, 9);
        Assert.Equal(sg, k1.QGenW, 9); Assert.Equal(sl, k1.QLossW, 9);
        Assert.Equal(qClamp, k1.QClampW); Assert.Equal(qHole, k1.QFromTubeW);
        Assert.Equal(sg - sl - qClamp + qHole, k1.ResidualW, 9);
        Assert.Equal(450.0, k1.TClampC);
        Assert.Equal(qClamp / (tNode - 450.0), k1.GEffWPerK, 12);
        Assert.Equal((450.0 - 25.0) / (tNode - 25.0), k1.FollowRatio, 12);
        Assert.Equal(sg / (iA * iA), k1.ResistanceRefOhm, 15);
        Assert.Equal(qHole / (tRoot - tNode), k1.HolePlateGWPerK, 12);
        Assert.Equal(k1.GEffWPerK * (1 - k1.FollowRatio), k1.StabClampWPerK, 12);

        // 不排除：被钉住的格也在节点里 ⇒ 它们的「发热 − 散热」归铜排；节点账的残差与排除时相同
        var k2 = RampTwoNode.CalibrateNode(mesh, tTwo, gen, loss, boundary, qClamp, qHole, tRoot, iA, pFix, excludeBoundaryCells: false);
        Assert.True(!k2.BoundaryCellsExcluded && k2.NodeCells == n && k2.TNodeC < tNode, $"不排除时均温 {k2.TNodeC}");
        Assert.Equal(mesh.VolumeMm3, k2.NodeVolumeMm3, 6);
        Assert.Equal(qClamp + gB - lB, k2.QClampW, 9);
        Assert.Equal(k1.ResidualW, k2.ResidualW, 9);

        // 模型在标定点上逐项还原合成场的节点账
        var gi = new RampTwoNode.Inputs
        {
            FlangeMassG = 300, FlangeAreaInsulMm2 = 700, FlangeAreaBareMm2 = 4000, HoleRadiusMm = 26, PlateEqOuterRadiusMm = 45, FlangeThickMm = 1.2,
            DesignCurrentA = iA, SharedFactor = 1.0, TabInsulThickMm = 5.0,
            FlangeResistanceRefOhm = k1.ResistanceRefOhm, FlangeRefTempC = k1.TNodeC,
            ClampConductanceWPerK = k1.GEffWPerK, ClampFollowRatio = k1.FollowRatio, ClampColdEndC = k1.TColdC,
            SurfaceCalibLossW = k1.QLossW, SurfaceCalibTempC = k1.TNodeC, HolePlateConductanceWPerK = k1.HolePlateGWPerK,
        };
        var model = new RampTwoNode.Model(pFix, gi);
        double tt = model.TubeNodeTempAtRoot(tRoot, k1.TNodeC, qHole);
        var f = model.Flows(0, tt, k1.TNodeC, iA);
        _out.WriteLine($"标定点：管节点 {tt:0.000} °C（管根 {tRoot}）　发热 {f.GenW:0.000}　表面 {f.SurfaceW:0.000}　铜排 {f.ClampW:0.000}　管来 {f.FromTubeW:0.000000}　热容 {f.StorageW:0.000}");
        Assert.True(tt > tRoot, "管孔流入为正时管节点应比管根热（翅片温降）");
        Assert.Equal(1.0, f.GenW / sg, 12);
        Assert.Equal(1.0, f.SurfaceW / sl, 12);
        Assert.Equal(1.0, f.ClampW / qClamp, 12);
        Assert.True(Math.Abs(f.FromTubeW / qHole - 1) <= 1e-6, $"管来 {f.FromTubeW} vs {qHole}");
        Assert.Equal(k1.ResidualW, f.StorageW, 6);
        Assert.Equal(450.0, model.ClampTempC(k1.TNodeC), 9);
        Assert.Equal(k1.TColdC, model.ClampTempC(k1.TColdC), 9);   // 冷态时夹持在冷端，不是一开始就 450

        // 热导边界：参考点是冷端，r = 0；自由端：没有通道
        var pG = new DesignInputs { BusbarClampTempC = 450, BusbarConductanceWPerK = 2.0, BusbarSinkTempC = 30 };
        var noBoundary = new bool[n];
        var k4 = RampTwoNode.CalibrateNode(mesh, tTwo, gen, loss, noBoundary, qClamp, qHole, tRoot, iA, pG, true);
        Assert.True(k4.Ok, k4.Note);
        Assert.Equal(ShellThermal.ClampBoundary.Conductance, k4.Mode);
        Assert.Equal(n, k4.NodeCells);                                  // 热导边界场里不钉格 ⇒ 压接格也在节点里
        Assert.Equal(30.0, k4.TClampC); Assert.Equal(0.0, k4.FollowRatio);
        Assert.Equal(qClamp / (k4.TNodeC - 30.0), k4.GEffWPerK, 12);
        Assert.Equal(k4.GEffWPerK, k4.StabClampWPerK, 15);
        var pFree = new DesignInputs { BusbarClampTempC = -1, BusbarConductanceWPerK = -1 };
        var k5 = RampTwoNode.CalibrateNode(null, null, null, null, null, 0.0, qHole, tRoot, iA, pFree, true);
        Assert.True(k5.ClampOk && !k5.Ok); Assert.Equal(0.0, k5.GEffWPerK); Assert.Equal(0.0, k5.StabClampWPerK);
        Assert.Equal(ShellThermal.ClampBoundary.Free, k5.Mode);

        // 标定不了的情形
        var bad = new (string name, RampTwoNode.NodeCalibration k, bool clampOk)[]
        {
            ("没有场", RampTwoNode.CalibrateNode(null, null, null, null, null, qClamp, qHole, tRoot, iA, pFix, true), false),
            ("逐格账长度不对", RampTwoNode.CalibrateNode(mesh, tTwo, gen.Take(n - 1).ToArray(), loss, boundary, qClamp, qHole, tRoot, iA, pFix, true), false),
            ("铜排热为负", RampTwoNode.CalibrateNode(mesh, tTwo, gen, loss, boundary, -5.0, qHole, tRoot, iA, pFix, true), false),
            ("均温不比夹持高 1 K", RampTwoNode.CalibrateNode(mesh, Enumerable.Repeat(450.5, n).ToArray(), gen, loss, boundary, qClamp, qHole, tRoot, iA, pFix, true), false),
            ("夹持低于冷端", RampTwoNode.CalibrateNode(mesh, tTwo, gen, loss, boundary, qClamp, qHole, tRoot, iA, new DesignInputs { BusbarClampTempC = 20, BusbarConductanceWPerK = -1, BusbarSinkTempC = 25 }, true), false),
            ("管孔流向与温差反号", RampTwoNode.CalibrateNode(mesh, tTwo, gen, loss, boundary, qClamp, -3.0, tRoot, iA, pFix, true), true),
            ("管根与均温差不足 1 K", RampTwoNode.CalibrateNode(mesh, tTwo, gen, loss, boundary, qClamp, qHole, tNode + 0.5, iA, pFix, true), true),
            ("没有电流", RampTwoNode.CalibrateNode(mesh, tTwo, gen, loss, boundary, qClamp, qHole, tRoot, 0.0, pFix, true), true),
        };
        foreach (var (name, kb, clampOk) in bad)
        {
            _out.WriteLine($"{name}：Ok {kb.Ok}　铜排项 {kb.ClampOk}　{kb.Note}");
            Assert.False(kb.Ok, name);
            Assert.Equal(clampOk, kb.ClampOk);
            Assert.Contains("标定不了", kb.Note);
            if (!clampOk) Assert.True(double.IsNaN(kb.GEffWPerK) && double.IsNaN(kb.StabClampWPerK), name + "：铜排项标定不了时导度应为 NaN，不许给个数顶上");
            else Assert.True(double.IsFinite(kb.StabClampWPerK), name + "：铜排项成了，整片热稳定应当判得了");
        }

        // 整片热稳定：给 NaN 判不了；给值就用这个值；几何导度总算一份、不随给的值变；不给时夹持项就是几何导度
        var q = new DesignInputs { BusbarClampTempC = 450 };
        FlangeStability.Result S(double? gClamp) => FlangeStability.Check(q, 500.0, 1100.0, 730, 4580, 10.0, 2 * 30 * 1.2, 150, 2 * Math.PI * 26 * 1.2, 4, 5.0, gClamp);
        var sNaN = S(double.NaN); var sVal = S(0.7); var sNull = S(null);
        _out.WriteLine($"热稳定：NaN → {sNaN.Note}；0.7 → 夹持 {sVal.DClampDT} 裕度 {sVal.Margin:0.000}（几何 {sVal.DClampGeomDT:0.0000} ⇒ {sVal.MarginGeomClamp:0.000}）；不给 → 夹持 {sNull.DClampDT:0.0000}（{sNull.ClampSource}）");
        Assert.True(sNaN.Undetermined);
        Assert.Contains("无法判定", sNaN.Note);
        Assert.DoesNotContain("热失控", sNaN.Note);
        Assert.True(sNaN.ClampFromField, "给了场标定值（哪怕是 NaN）出处位就该是场");
        Assert.False(sVal.Undetermined);
        Assert.Equal(0.7, sVal.DClampDT); Assert.True(sVal.ClampFromField);
        Assert.True(sVal.DClampGeomDT > 0 && sVal.DClampGeomDT != 0.7);
        Assert.Equal(sVal.DClampGeomDT, sNull.DClampGeomDT);
        Assert.Equal(sNull.DClampGeomDT, sNull.DClampDT); Assert.False(sNull.ClampFromField);
        // R48 G2 复审二（2026-09-15 Opus 5；审查意见 minor「门三把 MarginGeomClamp 的定义再写一遍去核它自己」）有意改断言：
        //   原 Assert.Equal((DSurfDT + DClampGeomDT + DTubeDT) / DGenDT, MarginGeomClamp, 12) → 现比两次 Check 的 Margin：把几何导度当场值传进去、与不传，都应逐位等于 MarginGeomClamp
        Assert.Equal(S(sVal.DClampGeomDT).Margin, sVal.MarginGeomClamp);
        Assert.Equal(sNull.Margin, sVal.MarginGeomClamp);
    }

    // ── 门四、门五的整线解（设计记录 W08 只跑一段）
    //   ★ 算例选择如实写明（2026-09-15 Opus 5，复审首跑）：原样的 W08 一段两片场里**管孔流入都为负**（入口 −19.0 W、出口 −36.6 W，热从法兰流进管），
    //     而节点均温比管根低（1100／1186 °C 对 1194／1236 °C）⇒ 管孔一项标定不了 —— 这正是「整片一个温度表示不了这条热流」的情形，
    //     升温两节点不解、判据表那一行写算不出来。于是这个算例改作「标定不了」那条路的门（四乙）；
    //     标定成得了的门（四甲、五）改用同一设计把两片舌厚改成 1.26 mm（两片管孔流入为正：入口 +21.4 W、出口 +25.6 W）。门的判读与容差没改。
    private static (LineCase lc, LineResult r, double seconds, DesignSpec d, DesignInputs p) W08OneSeg(double[]? tabThickMm)
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        if (tabThickMm is not null) d.TabThickMm = tabThickMm;
        // 舌保温用设计记录自己的（两端片 0.4／0.3 mm，都算包着）。注：试过整线改成 5 mm，出口片越过铂熔点、整线解不存在（2026-09-15 首跑），故不改。
        d = d.Fit();
        var lc = d.BuildCase(p, checkRamp: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = LineRunner.Run(lc);
        return (lc, r, sw.Elapsed.TotalSeconds, d, p);   // R48 G2 复审二（2026-09-15 Opus 5）：带回设计与输入，门调安装报告验「暂不给数」那一行
    }
    private static readonly Lazy<(LineCase lc, LineResult r, double seconds, DesignSpec d, DesignInputs p)> W08OneTab126 = new(() => W08OneSeg(new[] { 1.26, 1.26 }));
    private static readonly Lazy<(LineCase lc, LineResult r, double seconds, DesignSpec d, DesignInputs p)> W08One = new(() => W08OneSeg(null));

    /// <summary>R48 G2 复审二（2026-09-15 Opus 5）：安装报告判据表里某条判据那一行（按 Criteria.Plain 后的名字找，报告里名字就是这么印的）</summary>
    private static string ReportRow(string report, string key)
        => report.Split('\n').First(l => l.StartsWith(Criteria.Plain(key) + "\t", StringComparison.Ordinal));

    [Fact]
    public void 四乙_整线_记录模型缺陷_管孔流向与温差反号标定不了_界面暂不给数并说原因_热稳定仍判得了()
    {
        var (lc, r, secs, d, p) = W08One.Value;
        _out.WriteLine($"LineRunner.Run（W08 一段，原样）：{secs:0.0} s　Ok {r.Ok}　{r.Message}");
        Assert.True(r.Ok, r.Message);
        var o = LineRunner.FlangeLumped(lc, r.Flanges, excludeClampCells: true)!;
        var fw = r.Flanges[o.Index];
        var k = o.Calib;
        _out.WriteLine($"最不利片 {fw.Name}：管孔流入 {fw.QFromTubeW:+0.00;-0.00} W　管根 {fw.TRootC:0.0} °C　{k.Note}　铜排项 {k.ClampOk}　G {k.GEffWPerK:0.0000}　r {k.FollowRatio:0.0000}");
        Assert.True(fw.QFromTubeW < 0 && k.TNodeC < fw.TRootC, "本算例的前提（热从法兰流进管、节点均温低于管根）不成立，门量不到「标定不了」那条路");
        // R48 G2 复审二（2026-09-15 Opus 5）：下一句是**前提**（门量得到缺陷那条路），不是「标定不了才对」—— 模型改了让它标定得了，换算例，不改判读
        Assert.False(k.Ok, "本算例已能标定 ⇒ 门量不到缺陷那条路，换一个管孔流向与温差反号的算例");
        Assert.True(k.ClampOk, "铜排项应当标定得了");
        Assert.True(double.IsNaN(k.HolePlateGWPerK));
        Assert.Contains("整片一个温度表示不了", k.Note);
        Assert.Null(o.Ramp);
        Assert.Contains("标定不了", o.RampError);
        Assert.NotNull(o.RampIn);                     // 输入照样组（前后对照探针复现老口径要用），只是不解
        // 判据表：升温那一行算不出来（写原因）；整片热稳定只要铜排项，照样判得了，夹持取 G·(1 − r)
        var stab = r.Checks.First(c => c.Name.StartsWith(LineResult.Key.FlangeStab, StringComparison.Ordinal));
        var ramp = r.Checks.First(c => c.Name.StartsWith(LineResult.Key.RampField, StringComparison.Ordinal));
        _out.WriteLine("升温附注：" + ramp.Note);
        _out.WriteLine("热稳定附注：" + stab.Note);
        Assert.True(ramp.Undetermined && double.IsNaN(ramp.Actual));
        Assert.True(ramp.Withheld, "升温那一行复核前暂不给数，算不出来时也一样");
        Assert.Contains("暂不给数", ramp.Note);
        Assert.Contains("算不出来", ramp.Note);
        Assert.Contains("整片一个温度表示不了", ramp.Note);
        Assert.Contains("两节点模型的缺陷", ramp.Note);          // 缺陷写明是模型的，不让工程师以为设计出了问题
        Assert.DoesNotContain("-+", ramp.Note);                // 带号数不印负零
        Assert.DoesNotContain("AllOk", ramp.Note);             // 界面不许出现代码名
        Assert.DoesNotContain("偏保守", ramp.Note);
        Assert.False(stab.Undetermined, stab.Note);
        Assert.True(o.Stab.ClampFromField);
        Assert.Equal(k.GEffWPerK * (1 - k.FollowRatio), o.Stab.DClampDT);
        Assert.Equal(o.Stab.Margin, stab.Actual);
        // 安装报告：那一行印「参考（暂不给数）」，不印「参考（算不出）」
        string row = ReportRow(InstallReport.Build(r, d, p), LineResult.Key.RampField);
        _out.WriteLine("安装报告那一行：" + row);
        Assert.Contains("参考（暂不给数）", row);
        Assert.DoesNotContain("算不出", row);
    }

    [Fact]
    public void 四甲_整线_标定取自本片场_两处同一份_升温那一行判不了()
    {
        var (lc, r, secs, d, p) = W08OneTab126.Value;
        _out.WriteLine($"LineRunner.Run：{secs:0.0} s　Ok {r.Ok}　{r.Message}");
        Assert.True(r.Ok, r.Message);
        var o = LineRunner.FlangeLumped(lc, r.Flanges, excludeClampCells: true)!;
        var fw = r.Flanges[o.Index];
        var k = o.Calib;
        _out.WriteLine($"最不利片 {fw.Name}：{k.Note}　G {k.GEffWPerK:0.0000}　r {k.FollowRatio:0.0000}　孔侧 {k.HolePlateGWPerK:0.0000}　κ {o.SurfaceScale:0.0000}　节点 {k.NodeCells} 格　舌保温 {o.ThermalSetup.TabInsulThickMm}　热稳定夹持 {o.Stab.DClampDT}（{o.Stab.ClampSource}）");
        Assert.True(k.Ok, k.Note);
        Assert.True(o.MeshFromField);
        Assert.Same(fw.Mesh, o.Mesh);
        Assert.Equal(ShellThermal.ClampBoundary.FixedTemp, k.Mode);
        Assert.True(k.GEffWPerK > 0 && k.BoundaryCellsExcluded);
        // 取自本片场
        Assert.Equal(fw.QClampW, k.QClampW);
        Assert.Equal(fw.QFromTubeW, k.QFromTubeW);
        Assert.Equal(fw.TRootC, k.TRootC);
        Assert.Equal(fw.CurrentA, k.CurrentA);
        Assert.Equal(lc.ClampTempC[o.Index], k.TClampC);
        Assert.True(k.TNodeC > k.TClampC && k.TNodeC < fw.TMaxC + 1e-9);
        // 两处同一份：热稳定取 G·(1 − r)；升温两节点四项
        Assert.Equal(k.GEffWPerK * (1 - k.FollowRatio), o.Stab.DClampDT);
        Assert.True(o.Stab.ClampFromField);   // R48 G2 复审二（2026-09-15 Opus 5）有意改断言：原 Assert.Equal("稳态场标定", ClampSource)（比文字）→ 读出处位
        var gi = o.RampIn!;
        Assert.Equal(k.GEffWPerK, gi.ClampConductanceWPerK);
        Assert.Equal(k.FollowRatio, gi.ClampFollowRatio);
        Assert.Equal(k.TColdC, gi.ClampColdEndC);
        Assert.Equal(k.ResistanceRefOhm, gi.FlangeResistanceRefOhm);
        Assert.Equal(k.TNodeC, gi.FlangeRefTempC);
        Assert.Equal(k.QLossW, gi.SurfaceCalibLossW); Assert.Equal(k.TNodeC, gi.SurfaceCalibTempC);
        Assert.Equal(k.HolePlateGWPerK, gi.HolePlateConductanceWPerK);
        Assert.Equal(k.NodeVolumeMm3 * Materials.PtDensity * 1e-6, gi.FlangeMassG, 9);
        // 舌保温 = 本片热解那一份（PlateThermalInputs），而且确实包着、确实进了舌片区散热
        var ts = LineRunner.PlateThermalInputs(lc, o.Index, fw.CurrentA, fw.Mesh?.SourceField);
        Assert.Equal(ts.TabInsulThickMm, gi.TabInsulThickMm);
        double tab = gi.TabInsulThickMm;
        Assert.True(DesignScreen.FlangeFaceInsulated(tab), $"本片舌保温 {tab} mm 不算包着，门量不到舌保温");
        Assert.Equal(lc.DiscInsulEffectiveAt(o.Index), o.RampP!.FlangeInsulThickMm);
        var m = new RampTwoNode.Model(o.RampP!, gi);
        Assert.Equal(o.SurfaceScale, m.SurfaceScale);
        double want = 2e-6 * gi.FlangeAreaBareMm2 * DesignScreen.PlateFluxWPerM2(o.RampP!, 1000.0, tab);
        double bare = 2e-6 * gi.FlangeAreaBareMm2 * DesignScreen.PlateFluxWPerM2(o.RampP!, 1000.0, 0.0);
        Assert.NotEqual(bare, want);
        Assert.True(Math.Abs(m.FlangeSurfaceTabW(1000.0) / m.SurfaceScale / want - 1) <= 0.01, "整线：舌片区散热没按本片舌保温算");
        Assert.NotNull(o.Ramp);
        Assert.Equal(k.GEffWPerK, o.Ramp!.ClampConductanceWPerK);
        Assert.Equal(tab, o.Ramp.TabInsulThickMm);
        Assert.Equal(k.HolePlateGWPerK, o.Ramp.HolePlateConductanceWPerK);
        Assert.Equal(o.SurfaceScale, o.Ramp.SurfaceScale);

        // 判据表：热稳定就是这一份；升温那一行判不了、不给数，附注说清原因（审查意见 blocker）
        var stab = r.Checks.First(c => c.Name.StartsWith(LineResult.Key.FlangeStab, StringComparison.Ordinal));
        var ramp = r.Checks.First(c => c.Name.StartsWith(LineResult.Key.RampField, StringComparison.Ordinal));
        _out.WriteLine($"判据表：热稳定 {stab.Actual:R}（几何导度裕度 {o.Stab.MarginGeomClamp:R}）　升温 {ramp.Actual:R} 判不了 {ramp.Undetermined}（模型峰值 {o.Ramp.MaxFlangeMinusTubeK:R} K，不进表）");
        _out.WriteLine("升温附注：" + ramp.Note);
        _out.WriteLine("热稳定附注：" + stab.Note);
        Assert.Equal(o.Stab.Margin, stab.Actual);
        Assert.True(ramp.Undetermined, "升温那一行复核前应判不了");
        Assert.True(double.IsNaN(ramp.Actual), "升温那一行复核前不许给数");
        Assert.Equal(CheckKind.Reference, ramp.Kind);
        Assert.Contains("待复核", ramp.Note);
        Assert.Contains("不可引用", ramp.Note);
        // R48 G2 复审二（2026-09-15 Opus 5；审查意见 major「位置说法没有依据」）有意改断言：原 Contains("不代表孔边最热处") → 现「不代表片上最热处」且全文不提孔边
        Assert.Contains("不代表片上最热处", ramp.Note);
        Assert.DoesNotContain("孔边", ramp.Note);
        Assert.Contains("只在稳态那一点上", ramp.Note);          // 四项标定离开标定点不保证准，界面要说
        Assert.True(ramp.Withheld);
        Assert.DoesNotContain("-+", ramp.Note);
        Assert.Contains("铜排", ramp.Note);
        Assert.Contains($"{tab:0.0} mm 保温", ramp.Note);
        Assert.Contains("不与 215 K 比", ramp.Note);
        Assert.DoesNotContain("本条用两节点模型补上", ramp.Note);
        Assert.DoesNotContain("出现在", ramp.Note);       // 原附注「全程最大值，出现在 x h、当时管温 y °C」—— 不给数就不给时刻
        Assert.DoesNotContain("偏保守", ramp.Note);
        Assert.DoesNotContain("偏高", ramp.Note);
        Assert.Contains("稳态场标定", stab.Note);
        // R48 G2 复审二（2026-09-15 Opus 5；审查意见 minor「割线不是工程师的话」）有意改断言：原 Contains("割线") → 现不写「割线」、写人话「可能偏大」
        Assert.DoesNotContain("割线", stab.Note);
        Assert.Contains("可能偏大", stab.Note);
        Assert.Contains($"{o.Stab.MarginGeomClamp:0.00}", stab.Note);
        Assert.Equal(LineRunner.FlangeStabNote(o), stab.Note);   // 判据表那一行的附注就是公开函数那一份
        // 安装报告：升温那一行印「参考（暂不给数）」；热稳定那一行照常「参考（不卡交付）」
        string report = InstallReport.Build(r, d, p);
        string rampRow = ReportRow(report, LineResult.Key.RampField), stabRow = ReportRow(report, LineResult.Key.FlangeStab);
        _out.WriteLine("安装报告：" + rampRow + "　｜　" + stabRow);
        Assert.Contains("参考（暂不给数）", rampRow);
        Assert.Contains("参考（不卡交付）", stabRow);
        Assert.DoesNotContain("暂不给数", stabRow);
        Assert.Contains("暂不给数", report.Split('\n').First(l => l.Contains("升温期间共用片最先到温")));
    }

    [Fact]
    public void 五_场对账_标定点上模型逐项还原本片场的节点账()
    {
        var (lc, r, _, _, _) = W08OneTab126.Value;
        Assert.True(r.Ok, r.Message);
        var o = LineRunner.FlangeLumped(lc, r.Flanges, excludeClampCells: true)!;
        Assert.True(o.Calib.Ok && o.RampIn is not null, o.Calib.Note + o.RampError);
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            // 每片都对一次账（生产只用最不利片，但标定口径对每片都该成立）；最不利片直接用生产那一份输入
            var fw = r.Flanges[j];
            var mesh = fw.Mesh!;
            int n = mesh.CellCount;
            Assert.True(fw.CellGenW.Length == n && fw.CellLossW.Length == n && fw.FieldBoundaryCell.Length == n, $"{fw.Name}：整线结果没带逐格账");
            var ts = LineRunner.PlateThermalInputs(lc, j, fw.CurrentA, mesh.SourceField);
            var k = RampTwoNode.CalibrateNode(mesh, fw.TField, fw.CellGenW, fw.CellLossW, fw.FieldBoundaryCell,
                                              fw.QClampW, fw.QFromTubeW, fw.TRootC, fw.CurrentA, ts.P2, excludeBoundaryCells: true);
            Assert.True(k.Ok, $"{fw.Name}：{k.Note}");
            // 节点格 = 场的边界格取反 = 压接格取反
            Assert.True(mesh.ClampCell.Length == n, $"{fw.Name}：网格没有整面接触压接格");
            for (int c = 0; c < n; c++)
            {
                Assert.True(k.NodeCell[c] == !fw.FieldBoundaryCell[c], $"{fw.Name} 格 {c}：节点格与场的边界格对不上");
                Assert.True(k.NodeCell[c] == !mesh.ClampCell[c], $"{fw.Name} 格 {c}：定温边界下场的边界格与压接格对不上");
            }
            // 场侧（门自己对逐格账求和）
            double gen = 0, loss = 0;
            for (int c = 0; c < n; c++) if (!fw.FieldBoundaryCell[c]) { gen += fw.CellGenW[c]; loss += fw.CellLossW[c]; }

            RampTwoNode.Inputs gi;
            if (j == o.Index) gi = o.RampIn!;
            else
            {
                gi = o.RampIn!.Clone();
                gi.FlangeResistanceRefOhm = k.ResistanceRefOhm; gi.FlangeRefTempC = k.TNodeC;
                gi.SurfaceCalibLossW = k.QLossW; gi.SurfaceCalibTempC = k.TNodeC; gi.HolePlateConductanceWPerK = k.HolePlateGWPerK;
                gi.ClampConductanceWPerK = k.GEffWPerK; gi.ClampFollowRatio = k.FollowRatio; gi.ClampColdEndC = k.TColdC;
                gi.SharedFactor = fw.Shared ? Math.Sqrt(3.0) : 1.0;
            }
            var m = new RampTwoNode.Model(o.RampP!, gi);
            double iSeg = fw.CurrentA / gi.SharedFactor;
            double tt = m.TubeNodeTempAtRoot(fw.TRootC, k.TNodeC, fw.QFromTubeW);
            var f = m.Flows(0, tt, k.TNodeC, iSeg);
            double tolField = k.NodeCells * ShellThermal.ResidualRelTol * gen + 1e-9;
            _out.WriteLine($"{fw.Name}{(j == o.Index ? "（最不利，生产输入）" : "")}：节点 {k.NodeCells} 格、均温 {k.TNodeC:0.00} °C、管根 {fw.TRootC:0.00} °C、管节点 {tt:0.00} °C　"
                         + $"发热 模型 {f.GenW:0.000000} ↔ 场 {gen:0.000000}　表面 {f.SurfaceW:0.000000} ↔ {loss:0.000000}（κ {m.SurfaceScale:0.0000}）　"
                         + $"铜排 {f.ClampW:0.000000} ↔ {fw.QClampW:0.000000}　管来 {f.FromTubeW:0.000000} ↔ {fw.QFromTubeW:0.000000}　"
                         + $"热容吸收 {f.StorageW:0.000000} ↔ 场残差 {fw.EnergyResidualW:0.000000}（场残差上界 {tolField:0.000000}）　场收敛 {fw.FieldsConverged}");
            Assert.True(Math.Abs(f.GenW / gen - 1) <= 1e-9, $"{fw.Name}：发热对不上场（模型 {f.GenW}，场 {gen}）—— 参考电阻或参考温度不是节点口径");
            Assert.True(Math.Abs(f.SurfaceW / loss - 1) <= 1e-9, $"{fw.Name}：表面散热对不上场（模型 {f.SurfaceW}，场 {loss}）");
            Assert.True(Math.Abs(f.ClampW / fw.QClampW - 1) <= 1e-9, $"{fw.Name}：流进铜排对不上场（模型 {f.ClampW}，场 {fw.QClampW}）");
            Assert.True(Math.Abs(f.FromTubeW / fw.QFromTubeW - 1) <= 1e-6, $"{fw.Name}：管来对不上场（模型 {f.FromTubeW}，场 {fw.QFromTubeW}）");
            Assert.True(Math.Abs(f.StorageW - fw.EnergyResidualW) <= 1e-6 * gen, $"{fw.Name}：热容吸收 {f.StorageW} 与场的能量残差 {fw.EnergyResidualW} 对不上");
            Assert.True(fw.FieldsConverged, $"{fw.Name}：场没收敛，残差上界不成立");
            Assert.True(Math.Abs(fw.EnergyResidualW) <= tolField, $"{fw.Name}：场的能量残差 {fw.EnergyResidualW} W 超过收敛判据推出的上界 {tolField} W");
        }
    }
    [Fact]
    public void 六_界面附注_判不了要说原因_带号数不印负零()
    {
        // ── 带号数：负数取整成零印正零（net8 原样印「-+0.0」，2026-09-15 本机实测）；正常负数、正数逐字不变
        foreach (var (v, fmt, want) in new (double, string, string)[]
                 {
                     (-0.0, "+0.0;−0.0", "+0.0"), (-0.01, "+0.0;−0.0", "+0.0"), (-0.04, "+0.0;−0.0", "+0.0"), (-1e-9, "+0.0;−0.0", "+0.0"),
                     (-0.004, "+0.00;−0.00", "+0.00"), (-0.06, "+0.0;−0.0", "−0.1"), (-3.0, "+0.0;−0.0", "−3.0"), (0.01, "+0.0;−0.0", "+0.0"), (2.5, "+0.0;−0.0", "+2.5"),
                     (-0.01, "0.0", "0.0"),
                 })
        {
            string got = SizerResult.Signed(v, fmt);
            _out.WriteLine($"Signed({v:R}, \"{fmt}\") = \"{got}\"");
            Assert.Equal(want, got);
        }

        // ── 整片热稳定附注：夹持导度标定不出来 ⇒ 判不了，附注末尾写标定为什么不成
        var pFix = new DesignInputs { BusbarClampTempC = 450, BusbarConductanceWPerK = -1, BusbarSinkTempC = 25 };
        FlangeStability.Result S(DesignInputs q, double? g) => FlangeStability.Check(q, 500.0, 1100.0, 730, 4580, 10.0, 2 * 30 * 1.2, 150, 2 * Math.PI * 26 * 1.2, 4, 5.0, g);
        var kNoField = RampTwoNode.CalibrateNode(null, null, null, null, null, 200.0, 15.0, 1150.0, 1700.0, pFix, true);
        Assert.False(kNoField.ClampOk);
        var oBad = new LineRunner.FlangeLumpedOut { Stab = S(pFix, kNoField.StabClampWPerK), Calib = kNoField };
        string noteBad = LineRunner.FlangeStabNote(oBad);
        _out.WriteLine("判不了：" + noteBad);
        Assert.True(oBad.Stab.Undetermined && oBad.Stab.ClampFromField);
        Assert.Contains("无法判定", noteBad);
        Assert.Contains("原因：" + kNoField.Note, noteBad);
        Assert.DoesNotContain("热失控", noteBad);
        Assert.DoesNotContain("夹持导度 =", noteBad);

        // ── 标定成了（自由端 = 本来没有这条通道，导度 0 也算成）：追加夹持导度那一段，人话，不写「割线」
        var pFree = new DesignInputs { BusbarClampTempC = -1, BusbarConductanceWPerK = -1 };
        var kFree = RampTwoNode.CalibrateNode(null, null, null, null, null, 0.0, 15.0, 1150.0, 1700.0, pFree, true);
        Assert.True(kFree.ClampOk);
        var oFree = new LineRunner.FlangeLumpedOut { Stab = S(pFree, kFree.StabClampWPerK), Calib = kFree };
        string noteFree = LineRunner.FlangeStabNote(oFree);
        _out.WriteLine("标定成了：" + noteFree);
        Assert.False(oFree.Stab.Undetermined);
        Assert.Contains("舌端没有接铜排，夹持这一项为 0", noteFree);   // 自由端不说「流进铜排的热 ÷ 温差」那段空话
        Assert.DoesNotContain("夹持导度 =", noteFree);
        Assert.DoesNotContain("原因：", noteFree);
        // 定温边界、标定成了（合成网格，与门三同一组合成账）：追加夹持导度那一段，人话，不写「割线」
        var plate0 = new FlangePlate { HoleRadiusMm = 26.0 };
        var mesh0 = FlangeMesher.Build(plate0, 0, 4.0, 11.0, 50.0, 40.0, clampBandMm: double.NaN, clampFullFace: true);
        var b0 = mesh0.ClampCell.ToArray();
        var t0 = Enumerable.Range(0, mesh0.CellCount).Select(k => b0[k] ? 450.0 : 1000.0).ToArray();
        var g0 = Enumerable.Range(0, mesh0.CellCount).Select(k => 0.02 * mesh0.Area[k]).ToArray();
        var l0 = Enumerable.Range(0, mesh0.CellCount).Select(k => 0.01 * mesh0.Area[k]).ToArray();
        var kFix = RampTwoNode.CalibrateNode(mesh0, t0, g0, l0, b0, 200.0, 15.0, 1150.0, 1700.0, pFix, true);
        Assert.True(kFix.Ok, kFix.Note);
        var oFix = new LineRunner.FlangeLumpedOut { Stab = S(pFix, kFix.StabClampWPerK), Calib = kFix };
        string noteFix = LineRunner.FlangeStabNote(oFix);
        _out.WriteLine("定温标定成了：" + noteFix);
        Assert.Contains("夹持导度 =", noteFix);
        Assert.Contains("夹持温度", noteFix);
        Assert.Contains("可能偏大", noteFix);
        Assert.DoesNotContain("割线", noteFix);
        Assert.DoesNotContain("原因：", noteFix);
        // 不给场标定值（老算法）：出处位 false ⇒ 不追加
        var oGeom = new LineRunner.FlangeLumpedOut { Stab = S(pFix, null), Calib = kFree };
        Assert.False(oGeom.Stab.ClampFromField);
        Assert.DoesNotContain("夹持导度 =", LineRunner.FlangeStabNote(oGeom));

        // ── 管孔标定不成的原因：管孔流入落在 (−0.05, 0) 时不印「-+0.0」，并写明是两节点模型的缺陷
        var plate = new FlangePlate { HoleRadiusMm = 26.0 };
        var mesh = FlangeMesher.Build(plate, 0, 4.0, 11.0, 50.0, 40.0, clampBandMm: double.NaN, clampFullFace: true);
        int n = mesh.CellCount;
        var boundary = mesh.ClampCell.ToArray();
        var tTwo = Enumerable.Range(0, n).Select(k => boundary[k] ? 450.0 : 1000.0).ToArray();
        var gen = Enumerable.Range(0, n).Select(k => 0.02 * mesh.Area[k]).ToArray();
        var loss = Enumerable.Range(0, n).Select(k => 0.01 * mesh.Area[k]).ToArray();
        var kHole = RampTwoNode.CalibrateNode(mesh, tTwo, gen, loss, boundary, 200.0, -0.01, 1150.0, 1700.0, pFix, true);
        _out.WriteLine("管孔标定不成：" + kHole.Note);
        Assert.False(kHole.Ok);
        Assert.Contains("管孔流入 +0.0 W", kHole.Note);
        Assert.DoesNotContain("-+", kHole.Note);
        Assert.Contains("两节点模型的缺陷", kHole.Note);
    }
}
