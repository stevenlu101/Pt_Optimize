using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★ R48 J 路（2026-09-15 Opus 5，合并把关待办 P2-5）：「整面接触生效」只许有一个定义 —— <see cref="ShellMesh.ClampFullFaceActive"/>（长度 = 单元数且至少一格为真）。
///
/// 病：面上定温（ClampFaceActive）、两个求解器、集总模型排除压接格都只看「ClampCell 长度 = 单元数」，网格与热解配方指纹要求「至少一格为真」；
/// 生成器在压接段里一格形心都没有时仍写全假数组。扫描（deliverable/J路_J9_整面接触全假可达性_本次开跑于2026-09-15_190530.txt，W08 片0 板件）：
/// h = 4 mm 时压接长 0.5～3 mm 全部落进「长度 = 格数、没有一格为真」；h = 3 mm 时 0.5～2 mm；h = 2 mm（导航缺省）时 0.5～1 mm；h ≤ 0.5 mm 时不出现。
/// 界面压接长控件下限 3 mm ⇒ 界面缺省导航网格（2 mm）上够不着，网格 ≥ 4 mm 或探针、命令行给更短压接长时够得着。
///
/// 门（行为）：同一块板、同一张网格，ClampCell 为空（只钉外圈）与手工写成全假数组，两个求解器与集总面积逐位相同，而且都判「未生效」；
/// 生成器在压接长不到半个格时不写数组并在记录里说一句。改回任何一处「长度 = 单元数」的旧定义，热导模式下舌端均温走面积加权那一支，这里红。
/// </summary>
public class R48J_ClampFullFaceDefinitionGateTests
{
    [Fact]
    public void 全假压接数组与只钉外圈逐位相同_都判未生效()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone().Fit();
        var lc = d.BuildCase(p, checkRamp: false);
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        var g = lc.FlangePlates[0];
        const double h = 2.0, clampLen = 1.0;

        // 生成器：压接长不到半个格 ⇒ 不写全假数组
        var gen = FlangeMesher.Build(g, 0, h, lc.MeshCoarseMm, radius, clampLen, clampFullFace: true);
        Assert.Empty(gen.ClampCell);
        Assert.False(gen.ClampFullFaceActive);
        Assert.False(gen.Recipe!.ClampFullFace);
        Assert.Contains("压接段里没有格子", gen.ClampAnchorNote);

        // 同一块板、同一张网格：外圈口径 vs 手写全假数组（ClampCell 是公开字段，手造网格可以这么写）
        var outer = FlangeMesher.Build(g, 0, h, lc.MeshCoarseMm, radius, clampLen, clampFullFace: false);
        var allFalse = FlangeMesher.Build(g, 0, h, lc.MeshCoarseMm, radius, clampLen, clampFullFace: false);
        allFalse.ClampCell = new bool[allFalse.CellCount];
        Assert.Equal(outer.CellCount, allFalse.CellCount);
        Assert.False(allFalse.ClampFullFaceActive);
        Assert.False(allFalse.ClampFaceActive);
        Assert.Equal(outer.ClampSetCells(), allFalse.ClampSetCells());
        Assert.Equal(outer.ClampFaceCount(), allFalse.ClampFaceCount());
        Assert.Equal(DesignScreen.AreaByTangent(outer, g.Tangent().X, excludeClampCells: true),
                     DesignScreen.AreaByTangent(allFalse, g.Tangent().X, excludeClampCells: true));

        var p2 = SegmentSolver.Clone(p); p2.TSetC = 1150; p2.BusbarConductanceWPerK = 3.0;   // 热导模式：舌端均温按格数平均与按面积加权会不同
        double rho = Materials.PtResistivity(1150) * 1e3;
        var cO = ShellCurrent.SolveFor(lc, outer, 1200, rho, 1150);
        var cF = ShellCurrent.SolveFor(lc, allFalse, 1200, rho, 1150);
        Assert.Equal(cO.V, cF.V);
        Assert.Equal(cO.JMagAPerMm2, cF.JMagAPerMm2);
        var tO = ShellThermal.Solve(outer, cO.JMagAPerMm2, p2, 1140, g.InsulBoundaryXResolved, false, tabBoundaryX: g.Tangent().X, tabInsulThickMm: 2.0);
        var tF = ShellThermal.Solve(allFalse, cF.JMagAPerMm2, p2, 1140, g.InsulBoundaryXResolved, false, tabBoundaryX: g.Tangent().X, tabInsulThickMm: 2.0);
        Assert.True(tO.Converged && tF.Converged);
        Assert.Equal(tO.T, tF.T);
        Assert.Equal(tO.TTabEndMeanC, tF.TTabEndMeanC);
        Assert.Equal(tO.QToClampW, tF.QToClampW);
        Assert.Equal(tO.QFromTubeW, tF.QFromTubeW);
        Assert.False(tF.Recipe.ClampFullFace);
        Assert.Equal(tO.Recipe.Rule, tF.Recipe.Rule);
        // 非空转：同一张网格上真有压接格时，热导模式的舌端均温确实与外圈口径不同（否则上面的逐位相同证明不了什么）
        var full = FlangeMesher.Build(g, 0, h, lc.MeshCoarseMm, radius, lc.Base.BusbarClampLengthMm, clampFullFace: true);
        Assert.True(full.ClampFullFaceActive);

        // ★ 2026-09-17 Opus 5（J 路，独立复核「应修 5」）：**第五个消费者也进门** —— LineRunner.FlangeLumpedAt 里的
        //   `bool skip = excludeClampCells && mesh.ClampFullFaceActive`（集总模型的面积／体积／舌片导热长按它扣压接格）。
        //   此前这道门只验了 ShellCurrent／ShellThermal／ClampSetCells／AreaByTangent 四处；生成器已不写全假数组 ⇒ 现在无实害，但门保不住这一处。
        var lumpOuter = LineRunner.FlangeLumpedAt(lc, new[] { new FlangeOut { Name = "片0", Mesh = outer } }, 0)!;
        var lumpFalse = LineRunner.FlangeLumpedAt(lc, new[] { new FlangeOut { Name = "片0", Mesh = allFalse } }, 0)!;
        Assert.False(lumpOuter.ClampExcluded);
        Assert.False(lumpFalse.ClampExcluded);                   // 全假数组不算「整面接触生效」⇒ 不扣压接格
        Assert.Equal(lumpOuter.AreaMm2, lumpFalse.AreaMm2);
        Assert.Equal(lumpOuter.VolumeMm3, lumpFalse.VolumeMm3);
        Assert.Equal(lumpOuter.TabLenMm, lumpFalse.TabLenMm);
        Assert.Equal(lumpOuter.DiscAreaMm2, lumpFalse.DiscAreaMm2);
        Assert.Equal(lumpOuter.TabAreaMm2, lumpFalse.TabAreaMm2);
        // 非空转：真有压接格时这道开关确实改数（舌片导热长要减掉压接长）
        var lumpFull = LineRunner.FlangeLumpedAt(lc, new[] { new FlangeOut { Name = "片0", Mesh = full } }, 0)!;
        Assert.True(lumpFull.ClampExcluded);
        Assert.NotEqual(lumpOuter.TabLenMm, lumpFull.TabLenMm);
    }
}
