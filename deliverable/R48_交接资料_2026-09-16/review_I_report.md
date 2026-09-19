# I 路对抗审查结果（2026-09-16，只读；工作树 D:\WinForm\r48_I 已逐字节还原）

## 必须修：无
六条待办都真落地（I4／I5 有生产改动，其余是注释、测试、证据），我亲自复算的每个数都对上；三处注入门变红。下面是攻不下来的部分和几条应修。

## 应修（小，都能一分钟改）
1. **门 a 拿被测函数当自己的裁判**（`Pt_Optimize.Tests/R48InsulationSearchPlateFloorTests.cs:83-84`：`lo = Solver.ThickLowerCornerMm(...)` 再断言 `t >= lo`）。注入 D：把 `Solver.ThickLowerCornerMm` 的 `Math.Ceiling` 改成 `Math.Floor`（下角变 0.59 < 焊接屈曲/烧穿 0.60），**整个快套件 1008/1008 仍绿**（scratchpad\revI\fast_suite_injD.txt）。Solve 与保温搜索现在共用这一份，却没有一道门核它的定义。最小修：门 a 加两条与函数无关的断言 `lo >= d.DiscFloorMm(p) − 1e-12`、`lo − d.DiscFloorMm(p) < o.QuantThickMm`，并对 W08 写死期望 0.60。
2. **RunLayer 是否真走 LayerDesign 只有文本门守着**。注入 B（RunLayer 换回旧就地写法、不调 LayerDesign）：门 b 红、门 a 绿——行为门只测 `LayerDesign` 本身，不测搜索链路。且门 b 是整行字面匹配（`InsulationSearch.cs:978` 那句），改个变量名就误红。建议：慢探针 R48InsulationSearchProbeTests 重跑时加一条 `lr.PlateThickMm` 逐位等于 `LayerDesign(d0,…).TabThickMm`（造门在下游）。
3. **裁定 5 未做**：`docs/Pt_理论模型_v6.0.md:161`、`v6.1.md:214` 仍是「上界、确定、闭式」，与 `DesignCurrent.cs:21` 不一致（HANDOVER 自己也写了「没改」）。
4. `R48DeliverableWriteGuardTests.cs:32` 注释引 `deliverable/_bak_merge/snap/changed.txt` 作 8 份名单的出处——该文件只在 r48_M（未跟踪），本树没有。改引 scratchpad\revI 或 HANDOVER 里的实测清单，或把 8 份名单直接写进注释。
5. `R48LineDumpTests` 的「去文字」SHA 把文字**长度**写进去了（`Text()` 返回 `<文字 N 字>`），类注释说「说明文字改措辞不该动数」——改字数就动。改成不写长度即可（记录要重记）。
6. 备注（裁定 1 保留例外，但措辞要准）：12 份快套件回归产物里，孔形对比／孔位对比／形状族_对比／按场开槽_效果／移除优先级 **被 Core 注释引作出处**（DesignSpec.cs、Solver.cs、BranchMarks.cs 等），每跑快套件就换成当前数。HANDOVER 待办应写明「这 5 份是活证据」。

## 查过没问题（按 A–I）
| 项 | 结果 |
|---|---|
| A 落地 | I1 判词改写 + RecB2/RecBudget 重记（`R48G2RampClampChannelTests.cs:105-109`）；I2 新慢门 + 6 记录；I3 慢探针；I4 `Solver.ThickLowerCornerMm` + `InsulationSearch.LayerDesign`（生产改动只此两处，其余 4 个 Core 文件 diff 去掉 `//` 行后为空）；I5 注释；I6 ①～⑤ 全做 |
| B 注入 | A（LayerDesign 不置下角）→ 门 a 红「传入 2/2/2/2 得 2/2/2/2」；B → 门 b 红；C（Solve 改回就地式子）→ 门 b 红；E（R48HoleBandTests 改回原文件名）→ WriteGuard 红并点名 `:93`。四次都从备份还原、md5 相同 |
| C 逻辑 | ApplySectionFloor 全仓只 3 个调用点（InsulationSearch:955、Solver:350、:443）；DesignCurrent 尺寸链 `iQsPeak` 不读板厚，故 TubeLayerLowerBound 不受影响；NaN 传播与 Solve 起点同路径 |
| D 第二份实现 | 下角式子全仓只剩 `Solver.cs:1532`；克隆+换管保温+ApplySectionFloor 的流程只有 LayerDesign |
| E 行为无门 | 有门（见应修 1、2 的弱点） |
| F 数字一致 | 证据文件 1752/184706/191729/1949 与报告、HANDOVER、注释逐个核对相同；`0.4535682333333` 是 R 格式短串，1903 重跑「逐位相同」 |
| G UI | 没改 UI；InsulationSearch 在 `Pt_Optimize/UI` 无任何调用方（早已如此，保温搜索现场点不到，与 I 无关但要记着） |
| H 可比性 | I1 两步重放：第 1 步 vs r48_G2 原跑、第 2 步 vs 合并树 1536，我自己 diff 只差耗时一行；r48_G2 `.basetree`=460d3b、write-tree=be0011e；对照树汇总头的源文件 SHA-256 与 emerge/LineRunner_ours、Solver_ours、InsulationSearch_merger_prev、ShellThermal_prev、ShellMesh_prev 逐个相同，LineRunner_ours 无 SolveSegmentsInto、含 G3 行 ⇒ 确是 E 第一轮合入态；六算例四跑 SHA 相同、Records=终树 201310 跑；注入 AB 汇总 5 红 1 绿；三份指纹我按 `git diff HEAD --binary -- Pt_Optimize/Core | sha1` 独立重算得 6a6c9848／a28c71e0／a2f04291，全对 |
| I 越界 | J 路无非 deliverable 改动；K 路动 InsulationSearch.cs（hunk 在基线 79–437、727–892、1018+）与 LineRunner.cs（455–999、1532、1926、2335+），与 I 的 938–964、2056–2108、2754、2836、3633 不重叠；`git apply --check --3way r48_K/stream.patch` 只在 HANDOVER.md 报冲突（同位注记） |

## 我的测试命令与结果
- `dotnet build Pt_Optimize.sln` / `tests/UiWiring`：0 个错误
- `dotnet test … --filter "速度!=慢"`：已通过 1008/1008，6 m 15 s（scratchpad\revI\fast_suite_run1.txt）；跑后 8 份产物从 tar 还原，577 份 md5 与跑前相同
- PlateFloor 3 + WriteGuard 2 + HandoverGateCount + DocRef：11/11 通过
- 收尾：`git diff --stat` 空（工作区=暂存区）、无 INJECTION 残留、`stream.patch` 与 `git diff --cached --binary $(cat .basetree)` 逐字节相同（1846451 B）

## 仍开放 / 不确定
- 慢探针（LineDump 六算例、G2 两条、耦合阶梯、设计电流）我没有重跑，靠证据文件互核（SHA、指纹、diff）；E 两道慢门与 53 个改路径的慢测试仍未实跑（实施者已声明）。
- 引用扫描仍缺：`网格隔离_meshcmp2_片1_2026-09-13.txt`（在 Pt_Topo）；其余引用都在树里。
- 临时树 D:\WinForm\r48_I_G2F、r48_I_Eprev 未动。