using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★ R48 G1（2026-09-15，Opus 5 写；依据常驻数值把关人第十三、十四轮）：**配方指纹、证据防覆盖、压接盖孔改布尔判不了**的快门。
///
/// ══ 门
///   (a) 行为门（取代 R48ClampRecipeTests.f 的源码字符串检查作主证据）：生产链 LineRunner.Run（导航网格一次）算出来的**每一片**
///       网格配方 ShellMesh.Recipe.Rule == FlangeMesher.ProductionMeshRule、热解配方 ShellThermalResult.Recipe.Rule == ShellThermal.ProductionThermalRule，
///       LineResult.RecipeDeviations 为空；非空转：显式老口径网格、没落成节点的锚点、老表热解、孔边整格定温各自的规则都与生产不同；
///       2026-09-15 Opus 5（合并）加：压接形心整格（F 配方 ⑤ 关）的网格与热解规则都与生产不同、而且恰好只差「压接边界施加在面上」这一项。
///       ★ 2026-09-15 Opus 5（审查意见 minor）：舌端边界方式不进规则，原先只断言片 0 是定温 —— 改为**逐片**断言整份实际热解配方（含舌端方式、表节点数、孔边界面数）
///       等于用生产函数 LineRunner.PlateThermalInputs 组出本片输入、再经 ShellThermal.RecipeFor 预判出来的那一份（不手抄判定）；
///       另加非空转：网格上没有孔边界面（图纸孔比管外半径大出孔边判定带以外）⇒ 热解配方记「管温没有施加」，规则与生产不同。
///   (f) 压接段伸进圆盘（2026-09-15 Opus 5，审查意见 minor「同一种病只修了一半」）：网格布尔位 → FlangeOut.ClampIntoDisc → 吃法兰场的判据与参考行判不了，附注说人话；
///       几何判据「舌片自由段」与不吃法兰场的「管 J」不被这一遍标掉。
///   (b) 压接段盖到管孔：网格上的布尔位 ShellMesh.ClampCoversHole 为真（正常板为假）；LineRunner 不再按「；」切分 ClampAnchorNote 找「⚠」；
///       保温分界判得了（给了判据几何）而压接盖孔时，吃法兰场的判据照样判不了、附注说人话 —— 与保温分界那一遍分得开。
///       （保温分界也判不了的那块板的门在 TabInsulPerPlateTests「推不出切点的场」。）
///   (c) 证据头：git HEAD 与 Core 改动指纹是 40 位十六进制；探针源码指纹是调用方的相对路径 + 40 位十六进制；ForLineCase 的头含生产配方、耦合容差、共用片抽热、工况。
///   (d) 证据防覆盖：新文件照写；同头覆盖；头不同或没有头 ⇒ 不覆盖、改写到带时间戳的新文件、原文件逐字节不变；打开一次之后反复写落在同一个文件；
///       只有探针源码不同（Core 与配方都相同）也改道（2026-09-15 Opus 5，审查意见 minor）。
///   (e) 工单点名的五个探针都改用 EvidenceFile 写文件（源码门，辅助）。
/// </summary>
public class R48RecipeFingerprintTests
{
    private readonly ITestOutputHelper _out;
    public R48RecipeFingerprintTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void a_生产链每片网格与热解配方等于生产配方常量()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        var lc = d.BuildCase(p, checkRamp: false);
        var r = LineRunner.Run(lc);
        Assert.True(r.Ok, r.Message);
        Assert.NotEmpty(r.Flanges);
        _out.WriteLine("生产网格配方常量：" + FlangeMesher.ProductionMeshRule.Describe());
        _out.WriteLine("生产热解配方常量：" + ShellThermal.ProductionThermalRule.Describe());
        _out.WriteLine("整线汇总：" + r.RecipeSummary());
        foreach (var f in r.Flanges)
        {
            Assert.NotNull(f.MeshRecipe); Assert.NotNull(f.ThermalRecipe);
            _out.WriteLine($"{f.Name}：网格 {f.MeshRecipe!.Describe()}　热解 {f.ThermalRecipe!.Describe()}");
            Assert.Equal(FlangeMesher.ProductionMeshRule, f.MeshRecipe.Rule);
            Assert.Equal(ShellThermal.ProductionThermalRule, f.ThermalRecipe.Rule);
            // 带出来的就是网格与热解自己的那一份（不是另拼的）
            Assert.Same(f.Mesh!.Recipe, f.MeshRecipe);
            Assert.Equal(lc.MeshFineMm, f.MeshRecipe.HFineMm);
            Assert.Equal(FlangeMesher.ResolveClampBandMm(double.NaN, lc.MeshFineMm), f.MeshRecipe.ClampBandMm, 12);
            Assert.False(f.ClampCoversHole);
            // ★ 2026-09-15 Opus 5（审查意见 minor：混合开关原按输入重判）：热解配方的「分界格混合 开」现在是在求解里数出来的；
            //   再确认混合**真的发生了** —— 生产板舌片穿过保温分界圆（舌半宽 < 盘半径、舌片伸出圆外），分界圆上必有格子一部分有料在圆内、一部分在圆外 ⇒ 真混合格数 > 0。
            //   门槛由几何定、跑前写死，不看数。
            _out.WriteLine($"{f.Name}：真按份额混合的分界格 {f.ThermalRecipe.InsulBlendCells} 格");
            Assert.True(f.ThermalRecipe.InsulBlendCells > 0, f.Name + " 分界格混合记「开」却一格都没混合");
        }
        Assert.Empty(r.RecipeDeviations);
        AssertEachPlateRecipeFollowsProductionInputs(lc, r);

        // 图纸路径（LineRunner 走 BuildFromField 那一支）：同一组板栅格化成内存厚度场、判据几何给板本身 ⇒ 每片配方同样等于生产常量
        // 2026-09-15 Opus 5（审查意见 minor「行为门只核片 0 的舌端方式」）：这一次改跑两段（三片，中间一片是共用片），逐片核舌端方式等整份配方
        {
            var d2 = DesignSpec.W08.Clone();
            d2.SetpointC = new[] { 1150.0, 1150.0 }; d2.SegLengthMm = new[] { 300.0, 300.0 };
            d2 = d2.Fit();
            var lcF = d2.BuildCase(p, checkRamp: false);
            var plates = lcF.FlangePlates.Select(pl => { pl.HoleRadiusMm = lcF.TubeIdMm * 0.5 + lcF.WallMm; return pl; }).ToArray();
            lcF.FlangeFields = plates.Select(pl => AnalyticSurrogate.Rasterize(pl, 0.5, 2.0)).ToArray();
            lcF.GeomForJudge = plates;
            lcF.TabInsul3dmPerPlateMm = plates.Select(pl => pl.TabInsulThickMm).ToArray();
            lcF.FlangePlates = Array.Empty<FlangePlate>();
            var rF = LineRunner.Run(lcF);
            Assert.True(rF.Ok, rF.Message);
            _out.WriteLine("图纸路径汇总：" + rF.RecipeSummary());
            Assert.Equal(3, rF.Flanges.Length);
            Assert.Contains(rF.Flanges, fo => fo.Shared);
            Assert.All(rF.Flanges, fo =>
            {
                Assert.Equal(FlangeMesher.ProductionMeshRule, fo.MeshRecipe!.Rule);
                Assert.Equal(ShellThermal.ProductionThermalRule, fo.ThermalRecipe!.Rule);
            });
            Assert.Empty(rF.RecipeDeviations);
            AssertEachPlateRecipeFollowsProductionInputs(lcF, rF);

            // ★ 2026-09-23 决 101 A（RING）：图纸路径每片真包了「孔带按解析圆判料」（带宽 = 1 × 该片栅格步）；非空转：同一栅格改回（MeshRules.HoleBandCircle = false）的网格规则
            //   与生产恰好只差这一项的两份（量得、开关）—— 量得那一份在这块板上真的会变假（老栅格台阶料伸进孔圆），不是只抄开关。
            Assert.All(rF.Flanges, fo => Assert.True(fo.MeshRecipe!.HoleBandMm > 0, fo.Name + " 图纸路径没包孔带"));
            {
                var tf0 = lcF.FlangeFields[0];
                double rh0 = lcF.TubeIdMm * 0.5 + lcF.WallMm;
                var mBandOff = FlangeMesher.BuildFromMaterialWith(tf0, rh0, new MeshRules { HoleBandCircle = false }, 0, lcF.MeshFineMm, lcF.MeshCoarseMm, lcF.MeshFineRadiusMm,
                                                                  lcF.Base.BusbarClampLengthMm, lcF.MeshInnerMm, lcF.MeshInnerRadiusMm);
                _out.WriteLine($"图纸路径片0 生产：{rF.Flanges[0].MeshRecipe!.Describe()}");
                _out.WriteLine($"图纸路径片0 改回（孔带关）：{mBandOff.Recipe!.Describe()}");
                Assert.NotEqual(FlangeMesher.ProductionMeshRule, mBandOff.Recipe.Rule);
                Assert.False(mBandOff.Recipe.HoleBandCircle);
                Assert.Equal(FlangeMesher.ProductionMeshRule, mBandOff.Recipe.Rule with { HoleBandCircle = true, HoleBandCircleSwitch = true });
            }

            // 非空转：共用片的输入若换成自由端（夹持温度 −1、不给铜排热导），同一个预判给出的舌端方式就与这次实际的不同 —— 逐片比对看得见漂移
            int js = Array.FindIndex(rF.Flanges, fo => fo.Shared);
            var lcDrift = d2.BuildCase(p, checkRamp: false);
            lcDrift.FlangeFields = lcF.FlangeFields; lcDrift.GeomForJudge = lcF.GeomForJudge; lcDrift.TabInsul3dmPerPlateMm = lcF.TabInsul3dmPerPlateMm;
            lcDrift.FlangePlates = Array.Empty<FlangePlate>();
            lcDrift.Base.BusbarConductanceWPerK = -1;
            lcDrift.ClampTempC = Enumerable.Range(0, rF.Flanges.Length).Select(j => j == js ? -1.0 : (j < lcF.ClampTempC.Length ? lcF.ClampTempC[j] : lcF.Base.BusbarClampTempC)).ToArray();
            var setupDrift = LineRunner.PlateThermalInputs(lcDrift, js, rF.Flanges[js].CurrentA, rF.Flanges[js].Mesh!.SourceField);
            var predDrift = ShellThermal.RecipeFor(rF.Flanges[js].Mesh!, setupDrift.P2, insulDiscRadiusMm: setupDrift.InsulDiscRadiusMm);
            _out.WriteLine($"非空转：共用片 {rF.Flanges[js].Name} 输入改自由端后预判舌端 {predDrift.ClampMode}，这次实际 {rF.Flanges[js].ThermalRecipe!.ClampMode}");
            Assert.Equal(ClampBoundaryMode.Free, predDrift.ClampMode);
            Assert.NotEqual(predDrift.ClampMode, rF.Flanges[js].ThermalRecipe!.ClampMode);
        }

        // ── 非空转：几种非生产配方的规则都与常量不同
        var g = lc.FlangePlates[0];
        var mOld = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm,
                                      lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: 0, clampFullFace: false);
        _out.WriteLine("显式老口径网格：" + mOld.Recipe!.Describe());
        Assert.NotEqual(FlangeMesher.ProductionMeshRule, mOld.Recipe.Rule);
        Assert.False(mOld.Recipe.ClampFullFace); Assert.Equal(0.0, mOld.Recipe.ClampBandMm);
        Assert.True(mOld.Recipe.ClampAnchorOnNode);
        Assert.False(mOld.Recipe.ClampFaceDirichlet);                 // 只钉外圈：没有整面接触，面上定温不生效（2026-09-15 Opus 5（合并））

        // ★ 2026-09-15 Opus 5（合并）：**压接形心整格**（F 的 A 路：整面接触照开、细带取生产值，只把配方 ⑤ 关掉）——
        //   G1 的指纹写于 F 之前，分不开「面上定温」与「形心整格」。合并后网格与热解两份配方都要记下这一项，而且规则与生产常量**恰好只差这一项**。
        //   热解用本片生产输入（片 0 定温模式，上面逐片门已断言），电流密度取 0（只看配方）；Solve 数出来的与 RecipeFor 预判一致（不一致 Solve 会抛）。
        {
            var mCent = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm,
                                           lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: FlangeMesher.ResolveClampBandMm(double.NaN, lc.MeshFineMm),
                                           clampFullFace: true, clampFaceDirichlet: false);
            _out.WriteLine("压接形心整格网格：" + mCent.Recipe!.Describe());
            Assert.True(mCent.Recipe.ClampFullFace);
            Assert.False(mCent.ClampFaceActive);
            Assert.False(mCent.Recipe.ClampFaceDirichlet);
            Assert.Equal(0, mCent.Recipe.ClampFaceCount);
            Assert.NotEqual(FlangeMesher.ProductionMeshRule, mCent.Recipe.Rule);
            Assert.Equal(FlangeMesher.ProductionMeshRule, mCent.Recipe.Rule with { ClampFaceDirichlet = true });
            // 生产那一片的网格配方：面上定温、压接面数 > 0
            Assert.True(r.Flanges[0].MeshRecipe!.ClampFaceDirichlet);
            Assert.True(r.Flanges[0].MeshRecipe!.ClampFaceCount > 0);

            var sCent = LineRunner.PlateThermalInputs(lc, 0, r.Flanges[0].CurrentA, null);
            var thCent = ShellThermal.Solve(mCent, new double[mCent.CellCount], sCent.P2, r.Flanges[0].TRootC, sCent.InsulX, sCent.SymmetricInsul,
                                            tabBoundaryX: sCent.TabBoundaryX, tabInsulThickMm: sCent.TabInsulThickMm,
                                            discRadiusMm: sCent.DiscRadiusMm, insulDiscRadiusMm: sCent.InsulDiscRadiusMm);
            var predCent = ShellThermal.RecipeFor(mCent, sCent.P2, insulDiscRadiusMm: sCent.InsulDiscRadiusMm);
            _out.WriteLine("压接形心整格热解：预判「" + predCent.Describe() + "」　求解「" + thCent.Recipe.Describe() + "」");
            Assert.Equal(ClampBoundaryMode.Fixed, thCent.Recipe.ClampMode);
            Assert.True(thCent.Recipe.ClampFullFace);
            Assert.False(thCent.Recipe.ClampFaceDirichlet);
            Assert.Equal(0, thCent.Recipe.ClampFaceCount);
            Assert.Equal(predCent.Rule, thCent.Recipe.Rule);
            Assert.NotEqual(ShellThermal.ProductionThermalRule, thCent.Recipe.Rule);
            Assert.Equal(ShellThermal.ProductionThermalRule, thCent.Recipe.Rule with { ClampFaceDirichlet = true });
            Assert.Contains("按压接格形心整格", thCent.Recipe.Describe());
            // 生产那一片（定温）：面上那一支真走过、面数 > 0
            Assert.True(r.Flanges[0].ThermalRecipe!.ClampFaceDirichlet);
            Assert.True(r.Flanges[0].ThermalRecipe!.ClampFaceCount > 0);
            Assert.Contains("施加在压接面上", ShellThermal.ProductionThermalRule.Describe());
            Assert.Contains("施加在压接面上", FlangeMesher.ProductionMeshRule.Describe());
        }

        // 压接长不足一个最细格 ⇒ 锚点不落节点、也不铺细带（生成器照写记录）
        var mShort = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, 0.5 * lc.MeshFineMm,
                                        lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: double.NaN, clampFullFace: true);
        _out.WriteLine($"压接长 {0.5 * lc.MeshFineMm} mm 的网格：{mShort.Recipe!.Describe()}　记录：{mShort.ClampAnchorNote}");
        Assert.False(mShort.Recipe.ClampAnchorOnNode);
        Assert.Equal(0.0, mShort.Recipe.ClampBandMm);
        Assert.NotEqual(FlangeMesher.ProductionMeshRule, mShort.Recipe.Rule);
        // ★ 2026-09-15 Opus 5（审查意见 minor：两份配方对「整面接触」的定义不一致）：这张网格 ClampCell 长度 = 单元数但没有一格为真 ——
        //   网格配方记「未生效」，热解配方（预判与求解里数出来的）也必须记「只取压接外缘一圈」，不许再记「整个压接面」。
        // ★ 2026-09-15 Opus 5（J 路，合并把关待办 P2-5）有意改断言：原「ClampCell 长度 = 单元数、没有一格为真」→ 现「生成器不写全假的数组、整面接触判为未生效」。
        //   依据：两种定义并存的病根就是这张网格上的全假数组（求解器按长度判整面、指纹按至少一格判未生效）；生成器改为不写、全体读 ShellMesh.ClampFullFaceActive。
        //   下面「网格配方与热解配方都记未生效」的断言原样保留。行为门见 R48J_ClampFullFaceDefinitionGateTests。
        Assert.Empty(mShort.ClampCell);
        Assert.False(mShort.ClampFullFaceActive);
        Assert.Contains("压接段里没有格子", mShort.ClampAnchorNote);
        Assert.False(mShort.Recipe.ClampFullFace);
        var pShort = LineRunner.PlateThermalInputs(lc, 0, r.Flanges[0].CurrentA, null);
        var jShort = new double[mShort.CellCount];   // 只看配方，不看数：电流密度取 0 即可
        var thShort = ShellThermal.Solve(mShort, jShort, pShort.P2, r.Flanges[0].TRootC, pShort.InsulX, pShort.SymmetricInsul,
                                         tabBoundaryX: pShort.TabBoundaryX, tabInsulThickMm: pShort.TabInsulThickMm,
                                         discRadiusMm: pShort.DiscRadiusMm, insulDiscRadiusMm: pShort.InsulDiscRadiusMm);
        _out.WriteLine("同一张网格的热解配方：" + thShort.Recipe.Describe());
        Assert.False(thShort.Recipe.ClampFullFace);
        Assert.False(ShellThermal.RecipeFor(mShort, pShort.P2, insulDiscRadiusMm: pShort.InsulDiscRadiusMm).ClampFullFace);
        Assert.Equal(mShort.Recipe.ClampFullFace, thShort.Recipe.ClampFullFace);

        // 热解：老表（设定 + 200、60 节点）与孔边整格定温，规则都与常量不同；RecipeFor 与 Solve 填的一致
        var m0 = r.Flanges[0].Mesh!;
        var setup = LineRunner.PlateThermalInputs(lc, 0, r.Flanges[0].CurrentA, null);
        var thOldTab = ShellThermal.Solve(m0, r.Flanges[0].JField, setup.P2, r.Flanges[0].TRootC, setup.InsulX, setup.SymmetricInsul,
                                          tabBoundaryX: setup.TabBoundaryX, tabInsulThickMm: setup.TabInsulThickMm,
                                          discRadiusMm: setup.DiscRadiusMm, insulDiscRadiusMm: setup.InsulDiscRadiusMm,
                                          lossTableHiC: setup.P2.TSetC + 200, lossTableNodes: 60);
        _out.WriteLine("老表热解：" + thOldTab.Recipe.Describe());
        Assert.NotEqual(ShellThermal.ProductionThermalRule, thOldTab.Recipe.Rule);
        Assert.False(thOldTab.Recipe.LossTableNodesByRule);
        var recHole = ShellThermal.RecipeFor(m0, setup.P2, holeFaceDirichlet: false, insulDiscRadiusMm: setup.InsulDiscRadiusMm);
        Assert.NotEqual(ShellThermal.ProductionThermalRule, recHole.Rule);
        // 2026-09-15 Opus 5：孔边整格定温真解一次 —— 求解里数出来的（真钉住了孔边格）也记「钉住孔边整格」，与预判一致（不一致 Solve 会抛）
        var thHole = ShellThermal.Solve(m0, r.Flanges[0].JField, setup.P2, r.Flanges[0].TRootC, setup.InsulX, setup.SymmetricInsul,
                                        tabBoundaryX: setup.TabBoundaryX, tabInsulThickMm: setup.TabInsulThickMm, holeFaceDirichlet: false,
                                        discRadiusMm: setup.DiscRadiusMm, insulDiscRadiusMm: setup.InsulDiscRadiusMm);
        _out.WriteLine("孔边整格定温热解：" + thHole.Recipe.Describe());
        Assert.False(thHole.Recipe.HoleFaceDirichlet);
        Assert.Equal(recHole.Rule, thHole.Recipe.Rule);
        var recProd = ShellThermal.RecipeFor(m0, setup.P2, insulDiscRadiusMm: setup.InsulDiscRadiusMm);
        Assert.Equal(r.Flanges[0].ThermalRecipe!.Rule, recProd.Rule);
        Assert.Equal(r.Flanges[0].ThermalRecipe!.LossTableNodes, recProd.LossTableNodes);
        Assert.Equal(ClampBoundaryMode.Fixed, r.Flanges[0].ThermalRecipe!.ClampMode);

        // ★ 2026-09-15 Opus 5（审查意见 minor：网格上没有孔边界面时，热解配方原记「管温施加在孔边界面上」、规则等于生产）：
        //   构造 = 图纸孔半径比管外半径大 5 mm（> 孔边判定带 3 mm）⇒ 孔边一个面都判不成管孔 ⇒ 管温根本没施加。
        //   门槛跑前写死：孔边界面数 0、HoleFaceDirichlet 假、规则不等于生产、描述写「没有施加」；Solve 数出来的与 RecipeFor 预判一致（不一致 Solve 会抛）。
        {
            double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
            var gBig = d.BuildCase(p, checkRamp: false).FlangePlates[0];   // 另建一份板，不改本门整线用过的那块
            gBig.HoleRadiusMm = holeR + 5;
            var fBig = AnalyticSurrogate.Rasterize(gBig, 0.5, 2.0);
            var mNoHole = FlangeMesher.BuildFromField(fBig, holeR, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm,
                                                      lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
            Assert.DoesNotContain(mNoHole.Faces, fc => fc.B < 0 && fc.Tag == ShellMesh.TagHole);   // 构造成立
            var recNoHole = ShellThermal.RecipeFor(mNoHole, setup.P2, insulDiscRadiusMm: setup.InsulDiscRadiusMm);
            var thNoHole = ShellThermal.Solve(mNoHole, new double[mNoHole.CellCount], setup.P2, r.Flanges[0].TRootC, setup.InsulX, setup.SymmetricInsul,
                                              tabBoundaryX: setup.TabBoundaryX, tabInsulThickMm: setup.TabInsulThickMm,
                                              discRadiusMm: setup.DiscRadiusMm, insulDiscRadiusMm: setup.InsulDiscRadiusMm);
            _out.WriteLine("没有孔边界面的网格：预判「" + recNoHole.Describe() + "」　求解「" + thNoHole.Recipe.Describe() + "」");
            foreach (var rec in new[] { recNoHole, thNoHole.Recipe })
            {
                Assert.Equal(0, rec.HoleFaceCount);
                Assert.False(rec.HoleFaceDirichlet);
                Assert.False(rec.Rule.HasHoleBoundary);
                Assert.NotEqual(ShellThermal.ProductionThermalRule, rec.Rule);
                Assert.Contains("管温没有施加", rec.Describe());
                Assert.Contains("管温没有施加", rec.Rule.Describe());
            }
            Assert.True(r.Flanges[0].ThermalRecipe!.HoleFaceCount > 0);
            Assert.Contains("管温施加在孔边界面上", ShellThermal.ProductionThermalRule.Describe());
        }
    }

    /// <summary>
    /// ★ 2026-09-15 Opus 5（审查意见 minor「行为门只核片 0 的舌端方式」）：逐片 —— 整线实际热解配方 == 用生产函数组出的本片输入
    /// （LineRunner.PlateThermalInputs，与 RunOnce 逐片热解同一个入口）经 ShellThermal.RecipeFor 预判出来的那一份。
    /// 舌端方式不进规则（算例的选择），但它必须等于本片输入决定的方式；不手抄「夹持温度 ≥ 0 ⇒ 定温」这类判定。
    /// </summary>
    private void AssertEachPlateRecipeFollowsProductionInputs(LineCase c, LineResult r)
    {
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            var fo = r.Flanges[j];
            Assert.NotNull(fo.Mesh); Assert.NotNull(fo.ThermalRecipe);
            var setup = LineRunner.PlateThermalInputs(c, j, fo.CurrentA, fo.Mesh!.SourceField);
            var pred = ShellThermal.RecipeFor(fo.Mesh!, setup.P2, insulDiscRadiusMm: setup.InsulDiscRadiusMm);
            var act = fo.ThermalRecipe!;
            _out.WriteLine($"  逐片 {fo.Name}（共用片 {fo.Shared}）：实际舌端 {act.ClampMode}、预判 {pred.ClampMode}；孔边界面 {act.HoleFaceCount}；表 {act.LossTableNodes} 节点");
            Assert.Equal(pred.ClampMode, act.ClampMode);
            Assert.Equal(pred.Rule, act.Rule);
            Assert.Equal(pred.LossTableLoC, act.LossTableLoC);
            Assert.Equal(pred.LossTableNodes, act.LossTableNodes);
            Assert.Equal(pred.HoleFaceCount, act.HoleFaceCount);
            Assert.True(act.HoleFaceCount > 0, fo.Name + " 没有孔边界面");
        }
    }

    /// <summary>(b) 网格布尔位 + LineRunner 不读文字 + 保温分界判得了而压接盖孔时照样判不了。板同 R48ClampRecipeTests.e／TabInsulPerPlateTests「推不出切点的场」。</summary>
    [Fact]
    public void b_压接段盖到管孔改读布尔位_吃法兰场的判据判不了()
    {
        var p = new DesignInputs();
        var d = R47NavGridInstrumentTests.Disc56TwoSegs();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        // 2026-09-15 Opus 5（审查意见 minor）：开升温检查，「升温到位用时（集总）」在表上 ⇒ 下面按名单遍历时一并断言它判不了
        var lc = d.BuildCase(p, checkRamp: true);
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        var g = new FlangePlate { DiscRadiusMm = 28, HoleRadiusMm = holeR, TabEndXMm = 0, TabEndHalfWidthMm = 28, ThicknessMm = 1.0, TabParallel = true };
        var f = AnalyticSurrogate.Rasterize(g, 0.5, 2.0);
        Assert.True(lc.Base.BusbarClampLengthMm > 28, "本门要压接长盖过整片（材料 x ∈ [0, 28]）");

        var m = FlangeMesher.BuildFromField(f, holeR, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm,
                                            lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
        Assert.True(m.ClampCoversHole);
        Assert.True(m.Recipe!.ClampCoversHole);
        Assert.False(m.Recipe.ClampFullFace);
        Assert.NotEqual(FlangeMesher.ProductionMeshRule, m.Recipe.Rule);
        // 正常板：位为假
        var lcW = DesignSpec.W08.Clone().Fit().BuildCase(p, checkRamp: false);
        var mW = FlangeMesher.Build(lcW.FlangePlates[0], 0, lcW.MeshFineMm, lcW.MeshCoarseMm, lcW.MeshFineRadiusMm, lcW.Base.BusbarClampLengthMm,
                                    lcW.MeshInnerMm, lcW.MeshInnerRadiusMm, clampBandMm: double.NaN, clampFullFace: true);
        Assert.False(mW.ClampCoversHole);
        Assert.False(mW.ClampIntoDisc);

        // 源码（辅助）：LineRunner 不再按「；」切 ClampAnchorNote 找「⚠」
        string src = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "LineRunner.cs"));
        var code = string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Assert.DoesNotContain("ClampAnchorNote.Split", code);
        Assert.DoesNotContain("ClampAnchorNote", code);

        // 保温分界判得了（给判据几何 = 这块板本身）⇒ 判不了只能来自压接盖孔那一遍
        lc.FlangePlates = Array.Empty<FlangePlate>();
        lc.FlangeFields = new[] { f, f };
        lc.GeomForJudge = new[] { g, g };
        lc.TabInsul3dmPerPlateMm = new[] { 2.8, 2.8 };
        var r = LineRunner.Run(lc);
        Assert.True(r.Ok, r.Message);
        Assert.All(r.Flanges, fo => Assert.False(fo.InsulBoundaryUndetermined, fo.Name + " 本门要保温分界判得了"));
        Assert.All(r.Flanges, fo => Assert.True(fo.ClampCoversHole, fo.Name + " 应带压接盖孔位"));
        int seen = 0;
        foreach (var key in LineRunner.DependsOnFlangeFields)
            foreach (var ck in r.Checks.Where(c => c.Name.StartsWith(key, StringComparison.Ordinal)))
            {
                seen++;
                _out.WriteLine($"{Criteria.Plain(ck.Name)}：判不了 {ck.Undetermined}　{ck.Note}");
                Assert.True(ck.Undetermined, ck.Name + " 应无法判定");
                Assert.False(ck.Ok, ck.Name + " 判不了不算过");
                Assert.Contains("盖到了管孔", ck.Note);
                Assert.DoesNotContain("保温分界判不了", ck.Note);
                // 本遍加的那段（「（原注：」之前）说人话：不带网格内部说法与英文字段名；原注是各判据自己的，不归本门管
                string mine = ck.Note.Split("（原注：")[0];
                foreach (string jargon in new[] { "整面接触", "外圈", "孔边格", "边界面", "ClampCell", "clamp" })
                    Assert.DoesNotContain(jargon, mine);
            }
        Assert.True(seen >= 6, $"只找到 {seen} 条吃法兰场的判据／参考行");
        // 2026-09-15 Opus 5：集总升温用时（读逐片焦耳热与质量）在名单里、在表上、判不了
        Assert.Contains(LineResult.Key.RampHours, LineRunner.DependsOnFlangeFields);
        var ckRamp = r.Checks.Single(c => c.Name.StartsWith(LineResult.Key.RampHours, StringComparison.Ordinal));
        Assert.True(ckRamp.Undetermined, ckRamp.Note);
        Assert.Contains("盖到了管孔", ckRamp.Note);
        // 不吃法兰场的（管 J）不许被顺手标掉
        Assert.Contains(r.Checks, c => c.Name.StartsWith(LineResult.Key.TubeJ, StringComparison.Ordinal) && !c.Note.Contains("盖到了管孔"));
        Assert.False(r.AllOk);
        // 警告句仍进输出（每片一句），配方汇总里列出这两片与生产不同
        Assert.Equal(r.Flanges.Length, r.Notes.Count(s => s.StartsWith("⚠ ", StringComparison.Ordinal) && s.Contains("盖到了管孔")));
        Assert.Contains(r.RecipeDeviations, s => s.Contains("网格配方与生产不同"));
    }

    /// <summary>
    /// (f) ★ 2026-09-15 Opus 5（审查意见 minor「压接段伸进圆盘：同一种病只修了一半」）：网格位 ShellMesh.ClampIntoDisc → FlangeOut.ClampIntoDisc →
    /// 吃法兰场的判据与参考行一律判不了（MarkUndeterminedIfClampIntoDisc，名单同 DependsOnFlangeFields），附注说人话。
    /// 板：W08 一段算例的两块端片改成 盘半径 45、舌半宽 25（舌盘分界 x = −√(45² − 25²) ≈ −37.4 mm），舌尖放在让压接段内边落在
    /// 舌盘分界与「管孔半径 + 4 mm」正中间 —— 伸进了圆盘，但离管孔还远（盖不到管孔，与门 b 那种退化分开）。构造条件跑前写死并先断言成立。
    /// </summary>
    [Fact]
    public void f_压接段伸进圆盘改读布尔位_吃法兰场的判据判不了()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        var lc = d.BuildCase(p, checkRamp: true);
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm, clamp = lc.Base.BusbarClampLengthMm;
        foreach (var g in lc.FlangePlates)
        {
            g.DiscRadiusMm = 45; g.TabEndHalfWidthMm = 25; g.HoleRadiusMm = holeR; g.TabParallel = true;   // 等宽舌：舌盘分界与舌尖位置无关
            double xT = g.Tangent().X;
            double clampEnd = 0.5 * (xT + (-(holeR + 4)));
            g.TabEndXMm = clampEnd - clamp + g.ExtensionMm;       // 舌尖 x = TabEndXMm − ExtensionMm
            Assert.Equal(xT, g.Tangent().X);
            _out.WriteLine($"板：舌尖 {g.TabTipXMm:0.###}　舌盘分界 {xT:0.###}　压接段内边 {g.TabTipXMm + clamp:0.###}　管孔半径 {holeR}");
            Assert.False(g.TwoTabs);
            Assert.True(g.TabTipXMm + clamp > xT + 1, "构造：压接段要伸进圆盘");
            Assert.True(g.TabTipXMm + clamp < -(holeR + 3), "构造：压接段不许够到管孔");
        }
        var m = FlangeMesher.Build(lc.FlangePlates[0], 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, clamp,
                                   lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: double.NaN, clampFullFace: true);
        Assert.True(m.ClampIntoDisc);
        Assert.False(m.ClampCoversHole);

        var r = LineRunner.Run(lc);
        Assert.True(r.Ok, r.Message);
        Assert.All(r.Flanges, fo => Assert.True(fo.ClampIntoDisc, fo.Name + " 应带压接伸进圆盘位"));
        Assert.All(r.Flanges, fo => Assert.False(fo.ClampCoversHole, fo.Name + " 本门要压接段盖不到管孔"));
        Assert.All(r.Flanges, fo => Assert.False(fo.InsulBoundaryUndetermined));
        int seen = 0;
        foreach (var key in LineRunner.DependsOnFlangeFields)
            foreach (var ck in r.Checks.Where(c => c.Name.StartsWith(key, StringComparison.Ordinal)))
            {
                seen++;
                _out.WriteLine($"{Criteria.Plain(ck.Name)}：判不了 {ck.Undetermined}　{ck.Note}");
                Assert.True(ck.Undetermined, ck.Name + " 应无法判定");
                Assert.False(ck.Ok, ck.Name + " 判不了不算过");
                string mine = ck.Note.Split("（原注：")[0];
                Assert.Contains("伸进了圆盘", mine);
                Assert.Contains("请加长舌片或缩短压接长", mine);
                Assert.DoesNotContain("盖到了管孔", ck.Note);
                foreach (string jargon in new[] { "整面接触", "外圈", "孔边格", "边界面", "ClampCell", "clamp", "x =" })
                    Assert.DoesNotContain(jargon, mine);
            }
        Assert.True(seen >= 6, $"只找到 {seen} 条吃法兰场的判据／参考行");
        Assert.Contains(r.Checks, c => c.Name.StartsWith(LineResult.Key.RampHours, StringComparison.Ordinal) && c.Undetermined);
        // 不吃法兰场的（舌片自由段这条几何判据、管 J）不被这一遍标掉
        foreach (var key in new[] { LineResult.Key.FreeTab, LineResult.Key.TubeJ })
            Assert.Contains(r.Checks, c => c.Name.StartsWith(key, StringComparison.Ordinal) && !c.Note.Contains("伸进了圆盘"));
        Assert.False(r.AllOk);
        Assert.Equal(r.Flanges.Length, r.Notes.Count(s => s.StartsWith("⚠ ", StringComparison.Ordinal) && s.Contains("伸进了圆盘")));
    }

    [Fact]
    public void c_证据头写明代码与配方()
    {
        string root = HandoverDoc.Root();
        string head = EvidenceHeader.GitHead(root), sha = EvidenceHeader.CoreDiffSha1(root);
        _out.WriteLine($"git HEAD {head}　Core 改动指纹 {sha}");
        Assert.Matches("^[0-9a-f]{40}$", head);
        Assert.Matches("^[0-9a-f]{40}$", sha);
        Assert.Equal(sha, EvidenceHeader.CoreDiffSha1(root));              // 同一份代码两次算出同一个指纹
        // 2026-09-15 Opus 5（审查意见 minor）：探针源码指纹 = 调用方相对路径 + SHA1（编译器填调用方路径）
        string probe = EvidenceHeader.ProbeSourceFingerprint(ThisFile(), root);
        _out.WriteLine("探针源码指纹 " + probe);
        Assert.Matches("^Pt_Optimize\\.Tests/R48RecipeFingerprintTests\\.cs [0-9a-f]{40}$", probe);

        var p = new DesignInputs { SplitSharedFlangeDraw = true };
        var d = DesignSpec.W08.Clone().Fit();
        var lc = d.BuildCase(p, checkRamp: false);
        lc.CoupleTolK = 0.25;
        string h = EvidenceHeader.ForLineCase("门 c 标题", lc, root: root);
        _out.WriteLine(h);
        Assert.StartsWith(EvidenceHeader.BeginMark + "\n", h);
        Assert.EndsWith(EvidenceHeader.EndMark + "\n", h);
        Assert.Contains("# git HEAD：" + head, h);
        Assert.Contains(sha, h);
        Assert.Contains(FlangeMesher.ProductionMeshRule.Describe(), h);
        Assert.Contains(ShellThermal.ProductionThermalRule.Describe(), h);
        Assert.Contains("# 耦合容差：0.25 K", h);
        Assert.Contains("# 共用片抽热：各半（只算一次）", h);
        Assert.Contains("# 工况：带玻璃稳态", h);
        Assert.Contains("# 探针源码指纹（调用方源文件，SHA1）：" + probe + "\n", h);   // ForLineCase 把调用方路径原样往下传，不是 Evidence.cs 自己
        Assert.Equal(h, EvidenceHeader.Extract(h + "正文第一行\n第二行\n"));
        lc.EmptyTube = true; lc.Base.SplitSharedFlangeDraw = false;
        string h2 = EvidenceHeader.ForLineCase("门 c 标题", lc, root: root);
        Assert.Contains("# 工况：空管到温稳态（无玻璃）", h2);
        Assert.Contains("# 共用片抽热：双扣（两段各扣一次）", h2);
        Assert.NotEqual(h, h2);
    }

    [Fact]
    public void d_证据文件头不同就不覆盖()
    {
        string dir = Path.Combine(Path.GetTempPath(), "r48g1_evidence_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string root = HandoverDoc.Root();
            string hA = EvidenceHeader.Build("证据门", new[] { "网格 A" }, new[] { "热解 A" }, double.NaN, null, false, root: root);
            string hB = EvidenceHeader.Build("证据门", new[] { "网格 B" }, new[] { "热解 A" }, double.NaN, null, false, root: root);
            string file = Path.Combine(dir, "证据门样本.txt");

            // 新文件：照写；打开一次后反复写在同一个文件
            var ev = EvidenceFile.Open(file, hA);
            Assert.False(ev.Redirected); Assert.Equal(file, ev.Path);
            ev.Write("第一行\n"); ev.Write("第一行\n第二行\n");
            Assert.Equal(hA + "第一行\n第二行\n", File.ReadAllText(file));

            // 同头：覆盖
            var same = EvidenceFile.Write(file, hA, "重跑\n");
            Assert.False(same.Redirected);
            Assert.Equal(hA + "重跑\n", File.ReadAllText(file));

            // 头不同：不覆盖，改写到带时间戳的新文件；原文件逐字节不变；反复写都落在新文件
            byte[] before = File.ReadAllBytes(file);
            var now = new DateTime(2026, 9, 15, 10, 20, 30);
            var diff = EvidenceFile.Open(file, hB, now);
            _out.WriteLine("头不同：" + diff.Reason);
            Assert.True(diff.Redirected);
            Assert.NotEqual(file, diff.Path);
            Assert.Contains("_重跑20260915-102030", Path.GetFileName(diff.Path));
            Assert.Contains("证据头不同", diff.Reason);
            Assert.Contains("网格 B", diff.Reason);
            diff.Write("B 一\n"); diff.Write("B 一\nB 二\n");
            Assert.Equal(before, File.ReadAllBytes(file));
            Assert.Equal(hB + "B 一\nB 二\n", File.ReadAllText(diff.Path));
            Assert.Equal(2, Directory.GetFiles(dir).Length);
            // 同一秒再来一次：不撞名
            var diff2 = EvidenceFile.Open(file, hB, now);
            Assert.True(diff2.Redirected);
            Assert.NotEqual(diff.Path, diff2.Path);

            // 没有头的旧证据：不覆盖
            string old = Path.Combine(dir, "旧证据样本.txt");
            File.WriteAllText(old, "改动前写的旧证据，没有证据头\n", new UTF8Encoding(false));
            byte[] oldBytes = File.ReadAllBytes(old);
            var evOld = EvidenceFile.Write(old, hA, "新正文\n");
            _out.WriteLine("没有头：" + evOld.Reason);
            Assert.True(evOld.Redirected);
            Assert.Contains("没有证据头", evOld.Reason);
            Assert.Equal(oldBytes, File.ReadAllBytes(old));
            Assert.Equal(hA + "新正文\n", File.ReadAllText(evOld.Path));

            // ★ 2026-09-15 Opus 5（审查意见 minor）：Core、配方、工况全相同，**只有探针源码不同** ⇒ 头不同 ⇒ 也要改道（原先会整份覆盖）
            string srcDirA = Path.Combine(dir, "srcA"), srcDirB = Path.Combine(dir, "srcB");
            Directory.CreateDirectory(srcDirA); Directory.CreateDirectory(srcDirB);
            string probeA = Path.Combine(srcDirA, "某探针Tests.cs"), probeB = Path.Combine(srcDirB, "某探针Tests.cs");
            File.WriteAllText(probeA, "// 探针输入：保温 20 mm\r\n", new UTF8Encoding(false));
            File.WriteAllText(probeB, "// 探针输入：保温 25 mm\n", new UTF8Encoding(false));
            string hPA = EvidenceHeader.Build("证据门", new[] { "网格 A" }, new[] { "热解 A" }, double.NaN, null, false, root: root, callerFile: probeA);
            string hPB = EvidenceHeader.Build("证据门", new[] { "网格 A" }, new[] { "热解 A" }, double.NaN, null, false, root: root, callerFile: probeB);
            var onlyProbe = hPA.Split('\n').Except(hPB.Split('\n')).ToArray();
            Assert.Single(onlyProbe);
            Assert.StartsWith("# 探针源码指纹", onlyProbe[0]);
            string pfile = Path.Combine(dir, "只改探针样本.txt");
            EvidenceFile.Write(pfile, hPA, "保温 20 的数\n");
            byte[] pBefore = File.ReadAllBytes(pfile);
            var evP = EvidenceFile.Write(pfile, hPB, "保温 25 的数\n");
            _out.WriteLine("只改探针源码：" + evP.Reason);
            Assert.True(evP.Redirected);
            Assert.Contains("探针源码指纹", evP.Reason);
            Assert.Equal(pBefore, File.ReadAllBytes(pfile));
            // 行尾不同、内容相同 ⇒ 同一个指纹（autocrlf 不同的工作树不因行尾改道）
            File.WriteAllText(probeB, "// 探针输入：保温 20 mm\n", new UTF8Encoding(false));
            Assert.Equal(EvidenceHeader.ProbeSourceFingerprint(probeA, root), EvidenceHeader.ProbeSourceFingerprint(probeB, root));

            // 头格式不对：当场炸
            Assert.Throws<ArgumentException>(() => EvidenceFile.Open(Path.Combine(dir, "x.txt"), "不是证据头"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string f = "") => f;

    /// <summary>(e) 工单点名的五个证据探针改用 EvidenceFile（源码门，辅助）：写文件只走 EvidenceFile，不再 File.WriteAllText 整份覆盖。</summary>
    [Fact]
    public void e_五个证据探针改用证据文件写()
    {
        string dir = Path.Combine(HandoverDoc.Root(), "Pt_Optimize.Tests");
        foreach (var name in new[] { "R48SecondBatchTests", "R48TubeInsulScanTests", "R48SchemeATests", "R48ErrorBudgetTests", "R48ClampFullFaceTests" })
        {
            string s = File.ReadAllText(Path.Combine(dir, name + ".cs"));
            Assert.Contains("EvidenceFile.Open(", s);
            Assert.Contains("EvidenceHeader.", s);
            Assert.DoesNotMatch(new Regex(@"File\.WriteAllText\(\s*file\b"), s);
        }
    }
}
